using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;

namespace DataWarehouse.SDK.Infrastructure
{
    #region Interfaces

    /// <summary>
    /// Represents a point-in-time trace span for OpenTelemetry export.
    /// Contains all necessary information to reconstruct a span in OTLP format.
    /// </summary>
    /// <example>
    /// <code>
    /// var span = new TraceSpan
    /// {
    ///     TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
    ///     SpanId = Guid.NewGuid().ToString("N").Substring(0, 16),
    ///     OperationName = "storage.read",
    ///     StartTime = DateTimeOffset.UtcNow.AddMilliseconds(-150),
    ///     EndTime = DateTimeOffset.UtcNow,
    ///     Status = SpanStatus.Ok
    /// };
    /// </code>
    /// </example>
    public sealed class TraceSpan
    {
        /// <summary>
        /// Gets or sets the trace identifier (128-bit, hex encoded).
        /// </summary>
        public string TraceId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the span identifier (64-bit, hex encoded).
        /// </summary>
        public string SpanId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the parent span identifier, if this is a child span.
        /// </summary>
        public string? ParentSpanId { get; set; }

        /// <summary>
        /// Gets or sets the operation name for this span.
        /// </summary>
        public string OperationName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the span kind (Client, Server, Producer, Consumer, Internal).
        /// </summary>
        public SpanKind Kind { get; set; } = SpanKind.Internal;

        /// <summary>
        /// Gets or sets when the span started.
        /// </summary>
        public DateTimeOffset StartTime { get; set; }

        /// <summary>
        /// Gets or sets when the span ended.
        /// </summary>
        public DateTimeOffset EndTime { get; set; }

        /// <summary>
        /// Gets the duration of the span.
        /// </summary>
        public TimeSpan Duration => EndTime - StartTime;

        /// <summary>
        /// Gets or sets the span status.
        /// </summary>
        public SpanStatus Status { get; set; } = SpanStatus.Unset;

        /// <summary>
        /// Gets or sets the status description (typically set when status is Error).
        /// </summary>
        public string? StatusDescription { get; set; }

        /// <summary>
        /// Gets or sets the span attributes (key-value pairs).
        /// </summary>
        public Dictionary<string, object> Attributes { get; set; } = new();

        /// <summary>
        /// Gets or sets the span events (timestamped annotations).
        /// </summary>
        public List<SpanEvent> Events { get; set; } = new();

        /// <summary>
        /// Gets or sets links to related spans.
        /// </summary>
        public List<SpanLink> Links { get; set; } = new();

        /// <summary>
        /// Gets or sets the service name that produced this span.
        /// </summary>
        public string ServiceName { get; set; } = "DataWarehouse";

        /// <summary>
        /// Gets or sets the instrumentation scope name.
        /// </summary>
        public string InstrumentationScopeName { get; set; } = DataWarehouseInstrumentation.ServiceName;

        /// <summary>
        /// Gets or sets the instrumentation scope version.
        /// </summary>
        public string? InstrumentationScopeVersion { get; set; }
    }

    /// <summary>
    /// Represents an event within a trace span.
    /// </summary>
    public sealed class SpanEvent
    {
        /// <summary>
        /// Gets or sets the event name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the event timestamp.
        /// </summary>
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the event attributes.
        /// </summary>
        public Dictionary<string, object> Attributes { get; set; } = new();
    }

    /// <summary>
    /// Represents a link to a related span.
    /// </summary>
    public sealed class SpanLink
    {
        /// <summary>
        /// Gets or sets the linked span's trace ID.
        /// </summary>
        public string TraceId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the linked span's span ID.
        /// </summary>
        public string SpanId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the link attributes.
        /// </summary>
        public Dictionary<string, object> Attributes { get; set; } = new();
    }

    /// <summary>
    /// Defines the type of span in a distributed trace.
    /// </summary>
    public enum SpanKind
    {
        /// <summary>
        /// Internal span (default for local operations).
        /// </summary>
        Internal = 0,

        /// <summary>
        /// Server span (handling an incoming request).
        /// </summary>
        Server = 1,

        /// <summary>
        /// Client span (making an outgoing request).
        /// </summary>
        Client = 2,

        /// <summary>
        /// Producer span (producing a message to a queue).
        /// </summary>
        Producer = 3,

        /// <summary>
        /// Consumer span (consuming a message from a queue).
        /// </summary>
        Consumer = 4
    }

    /// <summary>
    /// Defines the status of a span.
    /// </summary>
    public enum SpanStatus
    {
        /// <summary>
        /// Status has not been set.
        /// </summary>
        Unset = 0,

        /// <summary>
        /// The operation completed successfully.
        /// </summary>
        Ok = 1,

        /// <summary>
        /// The operation resulted in an error.
        /// </summary>
        Error = 2
    }

    /// <summary>
    /// Defines the contract for exporting telemetry data to OpenTelemetry-compatible backends.
    /// Implementations should be thread-safe and handle batching internally if required.
    /// </summary>
    /// <example>
    /// <code>
    /// public class OtlpHttpExporter : IOpenTelemetryExporter
    /// {
    ///     private readonly HttpClient _client;
    ///     private readonly OpenTelemetryOptions _options;
    ///
    ///     public async Task&lt;ExportResult&gt; ExportMetricsAsync(MetricsSnapshot snapshot, CancellationToken ct)
    ///     {
    ///         // Convert to OTLP protobuf format and send
    ///         var request = ConvertToOtlpMetrics(snapshot);
    ///         var response = await _client.PostAsync(_options.MetricsEndpoint, request, ct);
    ///         return response.IsSuccessStatusCode ? ExportResult.Success : ExportResult.Failure;
    ///     }
    ///     // ... other methods
    /// }
    /// </code>
    /// </example>
    public interface IOpenTelemetryExporter
    {
        /// <summary>
        /// Exports a metrics snapshot to the configured backend.
        /// </summary>
        /// <param name="snapshot">The metrics snapshot to export.</param>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>A task representing the export result.</returns>
        Task<ExportResult> ExportMetricsAsync(MetricsSnapshot snapshot, CancellationToken cancellationToken = default);

        /// <summary>
        /// Exports a trace span to the configured backend.
        /// </summary>
        /// <param name="span">The trace span to export.</param>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>A task representing the export result.</returns>
        Task<ExportResult> ExportTraceAsync(TraceSpan span, CancellationToken cancellationToken = default);

        /// <summary>
        /// Exports log entries to the configured backend.
        /// </summary>
        /// <param name="entries">The log entries to export.</param>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>A task representing the export result.</returns>
        Task<ExportResult> ExportLogsAsync(LogEntry[] entries, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Represents the result of an export operation.
    /// </summary>
    public enum ExportResult
    {
        /// <summary>
        /// Export completed successfully.
        /// </summary>
        Success = 0,

        /// <summary>
        /// Export failed (retryable error).
        /// </summary>
        Failure = 1,

        /// <summary>
        /// Export dropped due to backpressure or queue full.
        /// </summary>
        Dropped = 2
    }

    #endregion

    #region Configuration

    /// <summary>
    /// Configuration options for OpenTelemetry integration.
    /// Supports environment variable configuration with OTEL_EXPORTER_* prefix.
    /// </summary>
    /// <example>
    /// <code>
    /// // Configure from code
    /// var options = new OpenTelemetryOptions
    /// {
    ///     Endpoint = "http://localhost:4317",
    ///     Protocol = OtlpProtocol.Grpc,
    ///     Headers = { ["Authorization"] = "Bearer token" },
    ///     BatchingOptions = new BatchingOptions { MaxBatchSize = 512 }
    /// };
    ///
    /// // Or load from environment variables
    /// var options = OpenTelemetryOptions.FromEnvironment();
    /// // Reads: OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_EXPORTER_OTLP_HEADERS, etc.
    /// </code>
    /// </example>
    public sealed class OpenTelemetryOptions
    {
        /// <summary>
        /// Environment variable name for the OTLP endpoint.
        /// </summary>
        public const string EnvEndpoint = "OTEL_EXPORTER_OTLP_ENDPOINT";

        /// <summary>
        /// Environment variable name for OTLP headers.
        /// </summary>
        public const string EnvHeaders = "OTEL_EXPORTER_OTLP_HEADERS";

        /// <summary>
        /// Environment variable name for OTLP protocol.
        /// </summary>
        public const string EnvProtocol = "OTEL_EXPORTER_OTLP_PROTOCOL";

        /// <summary>
        /// Environment variable name for export timeout.
        /// </summary>
        public const string EnvTimeout = "OTEL_EXPORTER_OTLP_TIMEOUT";

        /// <summary>
        /// Environment variable name for compression.
        /// </summary>
        public const string EnvCompression = "OTEL_EXPORTER_OTLP_COMPRESSION";

        /// <summary>
        /// Environment variable name for certificate file path.
        /// </summary>
        public const string EnvCertificate = "OTEL_EXPORTER_OTLP_CERTIFICATE";

        /// <summary>
        /// Environment variable name for client key file path.
        /// </summary>
        public const string EnvClientKey = "OTEL_EXPORTER_OTLP_CLIENT_KEY";

        /// <summary>
        /// Environment variable name for client certificate file path.
        /// </summary>
        public const string EnvClientCertificate = "OTEL_EXPORTER_OTLP_CLIENT_CERTIFICATE";

        /// <summary>
        /// Environment variable name for the service name.
        /// </summary>
        public const string EnvServiceName = "OTEL_SERVICE_NAME";

        /// <summary>
        /// Environment variable name for resource attributes.
        /// </summary>
        public const string EnvResourceAttributes = "OTEL_RESOURCE_ATTRIBUTES";

        /// <summary>
        /// Gets or sets the OTLP endpoint URL.
        /// For gRPC: typically "http://localhost:4317"
        /// For HTTP: typically "http://localhost:4318"
        /// </summary>
        public string Endpoint { get; set; } = "http://localhost:4317";

        /// <summary>
        /// Gets or sets the metrics-specific endpoint (overrides general endpoint for metrics).
        /// </summary>
        public string? MetricsEndpoint { get; set; }

        /// <summary>
        /// Gets or sets the traces-specific endpoint (overrides general endpoint for traces).
        /// </summary>
        public string? TracesEndpoint { get; set; }

        /// <summary>
        /// Gets or sets the logs-specific endpoint (overrides general endpoint for logs).
        /// </summary>
        public string? LogsEndpoint { get; set; }

        /// <summary>
        /// Gets or sets the OTLP protocol to use.
        /// </summary>
        public OtlpProtocol Protocol { get; set; } = OtlpProtocol.Grpc;

        /// <summary>
        /// Gets or sets custom headers to include in export requests.
        /// Common uses: authentication, routing, tenant identification.
        /// </summary>
        public Dictionary<string, string> Headers { get; set; } = new();

        /// <summary>
        /// Gets or sets the export timeout.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets or sets the compression algorithm to use.
        /// </summary>
        public OtlpCompression Compression { get; set; } = OtlpCompression.None;

        /// <summary>
        /// Gets or sets the path to the TLS certificate file.
        /// </summary>
        public string? CertificatePath { get; set; }

        /// <summary>
        /// Gets or sets the path to the client key file for mTLS.
        /// </summary>
        public string? ClientKeyPath { get; set; }

        /// <summary>
        /// Gets or sets the path to the client certificate file for mTLS.
        /// </summary>
        public string? ClientCertificatePath { get; set; }

        /// <summary>
        /// Gets or sets whether to skip TLS certificate verification.
        /// WARNING: Only use for development/testing.
        /// </summary>
        public bool InsecureSkipVerify { get; set; } = false;

        /// <summary>
        /// Gets or sets the service name for resource identification.
        /// </summary>
        public string ServiceName { get; set; } = DataWarehouseInstrumentation.ServiceName;

        /// <summary>
        /// Gets or sets the service version.
        /// </summary>
        public string? ServiceVersion { get; set; }

        /// <summary>
        /// Gets or sets the service instance ID.
        /// </summary>
        public string? ServiceInstanceId { get; set; }

        /// <summary>
        /// Gets or sets additional resource attributes.
        /// </summary>
        public Dictionary<string, string> ResourceAttributes { get; set; } = new();

        /// <summary>
        /// Gets or sets the batching configuration.
        /// </summary>
        public BatchingOptions Batching { get; set; } = new();

        /// <summary>
        /// Gets or sets whether metrics export is enabled.
        /// </summary>
        public bool MetricsEnabled { get; set; } = true;

        /// <summary>
        /// Gets or sets whether traces export is enabled.
        /// </summary>
        public bool TracesEnabled { get; set; } = true;

        /// <summary>
        /// Gets or sets whether logs export is enabled.
        /// </summary>
        public bool LogsEnabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the metrics export interval.
        /// </summary>
        public TimeSpan MetricsExportInterval { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Creates options from environment variables.
        /// </summary>
        /// <returns>Options configured from environment variables.</returns>
        public static OpenTelemetryOptions FromEnvironment()
        {
            var options = new OpenTelemetryOptions();

            var endpoint = Environment.GetEnvironmentVariable(EnvEndpoint);
            if (!string.IsNullOrEmpty(endpoint))
            {
                options.Endpoint = endpoint;
            }

            var headers = Environment.GetEnvironmentVariable(EnvHeaders);
            if (!string.IsNullOrEmpty(headers))
            {
                options.Headers = ParseHeaders(headers);
            }

            var protocol = Environment.GetEnvironmentVariable(EnvProtocol);
            if (!string.IsNullOrEmpty(protocol))
            {
                options.Protocol = protocol.ToLowerInvariant() switch
                {
                    "grpc" => OtlpProtocol.Grpc,
                    "http/protobuf" => OtlpProtocol.HttpProtobuf,
                    "http/json" => OtlpProtocol.HttpJson,
                    _ => OtlpProtocol.Grpc
                };
            }

            var timeout = Environment.GetEnvironmentVariable(EnvTimeout);
            if (!string.IsNullOrEmpty(timeout) && int.TryParse(timeout, out var timeoutMs))
            {
                options.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
            }

            var compression = Environment.GetEnvironmentVariable(EnvCompression);
            if (!string.IsNullOrEmpty(compression))
            {
                options.Compression = compression.ToLowerInvariant() switch
                {
                    "gzip" => OtlpCompression.Gzip,
                    "none" => OtlpCompression.None,
                    _ => OtlpCompression.None
                };
            }

            options.CertificatePath = Environment.GetEnvironmentVariable(EnvCertificate);
            options.ClientKeyPath = Environment.GetEnvironmentVariable(EnvClientKey);
            options.ClientCertificatePath = Environment.GetEnvironmentVariable(EnvClientCertificate);

            var serviceName = Environment.GetEnvironmentVariable(EnvServiceName);
            if (!string.IsNullOrEmpty(serviceName))
            {
                options.ServiceName = serviceName;
            }

            var resourceAttrs = Environment.GetEnvironmentVariable(EnvResourceAttributes);
            if (!string.IsNullOrEmpty(resourceAttrs))
            {
                options.ResourceAttributes = ParseHeaders(resourceAttrs);
            }

            return options;
        }

        private static Dictionary<string, string> ParseHeaders(string headerString)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pairs = headerString.Split(',', StringSplitOptions.RemoveEmptyEntries);

            foreach (var pair in pairs)
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2)
                {
                    headers[parts[0].Trim()] = parts[1].Trim();
                }
            }

            return headers;
        }

        /// <summary>
        /// Gets the effective endpoint for metrics export.
        /// </summary>
        public string GetMetricsEndpoint()
        {
            if (!string.IsNullOrEmpty(MetricsEndpoint))
            {
                return MetricsEndpoint;
            }

            return Protocol == OtlpProtocol.Grpc
                ? Endpoint
                : $"{Endpoint.TrimEnd('/')}/v1/metrics";
        }

        /// <summary>
        /// Gets the effective endpoint for traces export.
        /// </summary>
        public string GetTracesEndpoint()
        {
            if (!string.IsNullOrEmpty(TracesEndpoint))
            {
                return TracesEndpoint;
            }

            return Protocol == OtlpProtocol.Grpc
                ? Endpoint
                : $"{Endpoint.TrimEnd('/')}/v1/traces";
        }

        /// <summary>
        /// Gets the effective endpoint for logs export.
        /// </summary>
        public string GetLogsEndpoint()
        {
            if (!string.IsNullOrEmpty(LogsEndpoint))
            {
                return LogsEndpoint;
            }

            return Protocol == OtlpProtocol.Grpc
                ? Endpoint
                : $"{Endpoint.TrimEnd('/')}/v1/logs";
        }
    }

    /// <summary>
    /// Defines the OTLP protocol to use for exporting telemetry.
    /// </summary>
    public enum OtlpProtocol
    {
        /// <summary>
        /// gRPC protocol (default, most efficient).
        /// </summary>
        Grpc = 0,

        /// <summary>
        /// HTTP with Protobuf encoding.
        /// </summary>
        HttpProtobuf = 1,

        /// <summary>
        /// HTTP with JSON encoding (least efficient, but easiest to debug).
        /// </summary>
        HttpJson = 2
    }

    /// <summary>
    /// Defines compression algorithms for OTLP export.
    /// </summary>
    public enum OtlpCompression
    {
        /// <summary>
        /// No compression.
        /// </summary>
        None = 0,

        /// <summary>
        /// Gzip compression.
        /// </summary>
        Gzip = 1
    }

    /// <summary>
    /// Configuration options for batching telemetry data before export.
    /// </summary>
    public sealed class BatchingOptions
    {
        /// <summary>
        /// Gets or sets the maximum number of items in a batch.
        /// </summary>
        public int MaxBatchSize { get; set; } = 512;

        /// <summary>
        /// Gets or sets the maximum time to wait before exporting a batch.
        /// </summary>
        public TimeSpan MaxBatchDelay { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets the maximum queue size for pending export items.
        /// </summary>
        public int MaxQueueSize { get; set; } = 2048;

        /// <summary>
        /// Gets or sets the number of export worker threads.
        /// </summary>
        public int ExportWorkerCount { get; set; } = 1;

        /// <summary>
        /// Gets or sets whether to drop data when the queue is full.
        /// If false, the producer blocks until space is available.
        /// </summary>
        public bool DropOnQueueFull { get; set; } = true;
    }

    #endregion

    #region Metrics Adapter

    /// <summary>
    /// Adapts the DataWarehouse IMetricsCollector interface to OpenTelemetry's Meter API.
    /// Provides bidirectional translation between internal metrics and OpenTelemetry instruments.
    /// </summary>
    /// <example>
    /// <code>
    /// // Create the adapter with custom options
    /// var adapter = new OpenTelemetryMetricsAdapter(
    ///     metricsCollector,
    ///     new OpenTelemetryMetricsAdapterOptions
    ///     {
    ///         MetricPrefix = "datawarehouse",
    ///         DefaultAttributes = new Dictionary&lt;string, object&gt;
    ///         {
    ///             ["service.name"] = "my-service",
    ///             ["deployment.environment"] = "production"
    ///         }
    ///     });
    ///
    /// // The adapter automatically syncs metrics from IMetricsCollector to OTel instruments
    /// adapter.StartAutoSync(TimeSpan.FromSeconds(15));
    ///
    /// // Or manually record to OTel instruments
    /// adapter.RecordCounter("requests_total", 1, new KeyValuePair&lt;string, object?&gt;("method", "GET"));
    /// </code>
    /// </example>
    public sealed class OpenTelemetryMetricsAdapter : IDisposable
    {
        private readonly IMetricsCollector? _metricsCollector;
        private readonly Meter _meter;
        private readonly OpenTelemetryMetricsAdapterOptions _options;
        private readonly ConcurrentDictionary<string, Counter<long>> _counters;
        private readonly ConcurrentDictionary<string, Histogram<double>> _histograms;
        private readonly ConcurrentDictionary<string, ObservableGauge<double>> _gauges;
        private readonly ConcurrentDictionary<string, double> _gaugeValues;
        private readonly CancellationTokenSource _syncCts;
        private Task? _syncTask;
        private bool _disposed;

        /// <summary>
        /// Gets the underlying OpenTelemetry Meter.
        /// </summary>
        public Meter Meter => _meter;

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenTelemetryMetricsAdapter"/> class.
        /// </summary>
        /// <param name="metricsCollector">The metrics collector to adapt (optional).</param>
        /// <param name="options">Configuration options (optional).</param>
        public OpenTelemetryMetricsAdapter(
            IMetricsCollector? metricsCollector = null,
            OpenTelemetryMetricsAdapterOptions? options = null)
        {
            _metricsCollector = metricsCollector;
            _options = options ?? new OpenTelemetryMetricsAdapterOptions();
            _meter = new Meter(
                _options.MeterName ?? DataWarehouseInstrumentation.ServiceName,
                _options.MeterVersion ?? DataWarehouseInstrumentation.ServiceVersion);
            _counters = new ConcurrentDictionary<string, Counter<long>>(StringComparer.OrdinalIgnoreCase);
            _histograms = new ConcurrentDictionary<string, Histogram<double>>(StringComparer.OrdinalIgnoreCase);
            _gauges = new ConcurrentDictionary<string, ObservableGauge<double>>(StringComparer.OrdinalIgnoreCase);
            _gaugeValues = new ConcurrentDictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            _syncCts = new CancellationTokenSource();
        }

        /// <summary>
        /// Records a counter increment with the specified attributes.
        /// </summary>
        /// <param name="name">The counter name.</param>
        /// <param name="value">The value to add.</param>
        /// <param name="attributes">Optional attributes/tags.</param>
        public void RecordCounter(string name, long value, params KeyValuePair<string, object?>[] attributes)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(name);

            var fullName = GetFullMetricName(name);
            var counter = _counters.GetOrAdd(fullName, n =>
                _meter.CreateCounter<long>(n, description: $"Counter for {name}"));

            var mergedAttributes = MergeAttributes(attributes);
            counter.Add(value, mergedAttributes);
        }

        /// <summary>
        /// Records a histogram value with the specified attributes.
        /// </summary>
        /// <param name="name">The histogram name.</param>
        /// <param name="value">The value to record.</param>
        /// <param name="unit">Optional unit description.</param>
        /// <param name="attributes">Optional attributes/tags.</param>
        public void RecordHistogram(string name, double value, string? unit = null, params KeyValuePair<string, object?>[] attributes)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(name);

            var fullName = GetFullMetricName(name);
            var histogram = _histograms.GetOrAdd(fullName, n =>
                _meter.CreateHistogram<double>(n, unit, description: $"Histogram for {name}"));

            var mergedAttributes = MergeAttributes(attributes);
            histogram.Record(value, mergedAttributes);
        }

        /// <summary>
        /// Records a gauge value with the specified attributes.
        /// </summary>
        /// <param name="name">The gauge name.</param>
        /// <param name="value">The current value.</param>
        /// <param name="unit">Optional unit description.</param>
        /// <param name="attributes">Optional attributes/tags.</param>
        public void RecordGauge(string name, double value, string? unit = null, params KeyValuePair<string, object?>[] attributes)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(name);

            var fullName = GetFullMetricName(name);
            var gaugeKey = BuildGaugeKey(fullName, attributes);

            _gaugeValues[gaugeKey] = value;

            // Create observable gauge if not exists
            _gauges.GetOrAdd(fullName, n =>
                _meter.CreateObservableGauge(
                    n,
                    () => GetGaugeValues(fullName),
                    unit,
                    description: $"Gauge for {name}"));
        }

        /// <summary>
        /// Starts automatic synchronization from IMetricsCollector to OpenTelemetry instruments.
        /// </summary>
        /// <param name="interval">The sync interval.</param>
        public void StartAutoSync(TimeSpan interval)
        {
            ThrowIfDisposed();

            if (_metricsCollector == null)
            {
                throw new InvalidOperationException("No IMetricsCollector was provided. Cannot start auto-sync.");
            }

            if (_syncTask != null)
            {
                throw new InvalidOperationException("Auto-sync is already running.");
            }

            _syncTask = Task.Run(async () =>
            {
                while (!_syncCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(interval, _syncCts.Token).ConfigureAwait(false);
                        SyncFromCollector();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        // Log error but continue syncing
                    }
                }
            }, _syncCts.Token);
        }

        /// <summary>
        /// Manually synchronizes metrics from the IMetricsCollector to OpenTelemetry instruments.
        /// </summary>
        public void SyncFromCollector()
        {
            ThrowIfDisposed();

            if (_metricsCollector == null)
            {
                return;
            }

            var snapshot = _metricsCollector.GetSnapshot();

            // Sync counters
            foreach (var (key, metric) in snapshot.Counters)
            {
                var fullName = GetFullMetricName(metric.Name);
                var attributes = ConvertTagsToAttributes(metric.Tags);

                var counter = _counters.GetOrAdd(fullName, n =>
                    _meter.CreateCounter<long>(n, description: $"Counter for {metric.Name}"));

                // Note: Counters are cumulative, so we just record the current value
                // In a real implementation, you'd track deltas
                counter.Add(metric.Value, attributes);
            }

            // Sync gauges
            foreach (var (key, metric) in snapshot.Gauges)
            {
                var fullName = GetFullMetricName(metric.Name);
                var attributes = ConvertTagsToAttributes(metric.Tags);
                var gaugeKey = BuildGaugeKey(fullName, attributes);

                _gaugeValues[gaugeKey] = metric.Value;

                _gauges.GetOrAdd(fullName, n =>
                    _meter.CreateObservableGauge(
                        n,
                        () => GetGaugeValues(fullName),
                        description: $"Gauge for {metric.Name}"));
            }

            // Sync histograms
            foreach (var (key, metric) in snapshot.Histograms)
            {
                var fullName = GetFullMetricName(metric.Name);
                var attributes = ConvertTagsToAttributes(metric.Tags);

                var histogram = _histograms.GetOrAdd(fullName, n =>
                    _meter.CreateHistogram<double>(n, description: $"Histogram for {metric.Name}"));

                // Record summary statistics as histogram observations
                // Note: This is a simplification; real OTel histograms track buckets
                if (metric.Count > 0)
                {
                    histogram.Record(metric.Mean, attributes);
                }
            }
        }

        /// <summary>
        /// Converts a MetricsSnapshot to OpenTelemetry-compatible format for export.
        /// </summary>
        /// <param name="snapshot">The metrics snapshot to convert.</param>
        /// <returns>A collection of OTel-compatible metric data points.</returns>
        public IEnumerable<OtelMetricDataPoint> ConvertSnapshot(MetricsSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            var results = new List<OtelMetricDataPoint>();

            foreach (var (key, metric) in snapshot.Counters)
            {
                results.Add(new OtelMetricDataPoint
                {
                    Name = GetFullMetricName(metric.Name),
                    Type = OtelMetricType.Counter,
                    Value = metric.Value,
                    Attributes = ConvertTagsToDictionary(metric.Tags),
                    Timestamp = snapshot.Timestamp
                });
            }

            foreach (var (key, metric) in snapshot.Gauges)
            {
                results.Add(new OtelMetricDataPoint
                {
                    Name = GetFullMetricName(metric.Name),
                    Type = OtelMetricType.Gauge,
                    Value = metric.Value,
                    Attributes = ConvertTagsToDictionary(metric.Tags),
                    Timestamp = snapshot.Timestamp
                });
            }

            foreach (var (key, metric) in snapshot.Histograms)
            {
                results.Add(new OtelMetricDataPoint
                {
                    Name = GetFullMetricName(metric.Name),
                    Type = OtelMetricType.Histogram,
                    HistogramData = new OtelHistogramData
                    {
                        Count = metric.Count,
                        Sum = metric.Sum,
                        Min = metric.Min,
                        Max = metric.Max,
                        Percentiles = new Dictionary<double, double>
                        {
                            [0.50] = metric.P50,
                            [0.95] = metric.P95,
                            [0.99] = metric.P99
                        }
                    },
                    Attributes = ConvertTagsToDictionary(metric.Tags),
                    Timestamp = snapshot.Timestamp
                });
            }

            return results;
        }

        private string GetFullMetricName(string name)
        {
            if (string.IsNullOrEmpty(_options.MetricPrefix))
            {
                return name;
            }

            return $"{_options.MetricPrefix}.{name}";
        }

        private KeyValuePair<string, object?>[] MergeAttributes(KeyValuePair<string, object?>[] attributes)
        {
            if (_options.DefaultAttributes.Count == 0)
            {
                return attributes;
            }

            var merged = new List<KeyValuePair<string, object?>>();

            foreach (var kvp in _options.DefaultAttributes)
            {
                merged.Add(new KeyValuePair<string, object?>(kvp.Key, kvp.Value));
            }

            merged.AddRange(attributes);
            return merged.ToArray();
        }

        private static KeyValuePair<string, object?>[] ConvertTagsToAttributes(string[] tags)
        {
            var attributes = new List<KeyValuePair<string, object?>>();

            foreach (var tag in tags)
            {
                var parts = tag.Split(':', 2);
                if (parts.Length == 2)
                {
                    attributes.Add(new KeyValuePair<string, object?>(parts[0], parts[1]));
                }
                else
                {
                    attributes.Add(new KeyValuePair<string, object?>(tag, true));
                }
            }

            return attributes.ToArray();
        }

        private static Dictionary<string, object> ConvertTagsToDictionary(string[] tags)
        {
            var dict = new Dictionary<string, object>();

            foreach (var tag in tags)
            {
                var parts = tag.Split(':', 2);
                if (parts.Length == 2)
                {
                    dict[parts[0]] = parts[1];
                }
                else
                {
                    dict[tag] = true;
                }
            }

            return dict;
        }

        private static string BuildGaugeKey(string name, KeyValuePair<string, object?>[] attributes)
        {
            if (attributes.Length == 0)
            {
                return name;
            }

            var sb = new StringBuilder(name);
            sb.Append('{');

            var first = true;
            foreach (var attr in attributes.OrderBy(a => a.Key))
            {
                if (!first) sb.Append(',');
                sb.Append(attr.Key).Append('=').Append(attr.Value);
                first = false;
            }

            sb.Append('}');
            return sb.ToString();
        }

        private IEnumerable<Measurement<double>> GetGaugeValues(string baseName)
        {
            foreach (var kvp in _gaugeValues)
            {
                if (kvp.Key.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                {
                    yield return new Measurement<double>(kvp.Value);
                }
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        /// <summary>
        /// Disposes the metrics adapter.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _syncCts.Cancel();

            try
            {
                _syncTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Ignore cancellation exceptions
            }

            _syncCts.Dispose();
            _meter.Dispose();
        }
    }

    /// <summary>
    /// Configuration options for the OpenTelemetry metrics adapter.
    /// </summary>
    public sealed class OpenTelemetryMetricsAdapterOptions
    {
        /// <summary>
        /// Gets or sets the name for the OpenTelemetry Meter.
        /// </summary>
        public string? MeterName { get; set; }

        /// <summary>
        /// Gets or sets the version for the OpenTelemetry Meter.
        /// </summary>
        public string? MeterVersion { get; set; }

        /// <summary>
        /// Gets or sets a prefix to add to all metric names.
        /// </summary>
        public string? MetricPrefix { get; set; }

        /// <summary>
        /// Gets or sets default attributes to add to all metrics.
        /// </summary>
        public Dictionary<string, object> DefaultAttributes { get; set; } = new();
    }

    /// <summary>
    /// Represents an OpenTelemetry-compatible metric data point.
    /// </summary>
    public sealed class OtelMetricDataPoint
    {
        /// <summary>
        /// Gets or sets the metric name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the metric type.
        /// </summary>
        public OtelMetricType Type { get; set; }

        /// <summary>
        /// Gets or sets the metric value (for counters and gauges).
        /// </summary>
        public double Value { get; set; }

        /// <summary>
        /// Gets or sets histogram data (for histogram type).
        /// </summary>
        public OtelHistogramData? HistogramData { get; set; }

        /// <summary>
        /// Gets or sets the metric attributes.
        /// </summary>
        public Dictionary<string, object> Attributes { get; set; } = new();

        /// <summary>
        /// Gets or sets the data point timestamp.
        /// </summary>
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Defines the types of OpenTelemetry metrics.
    /// </summary>
    public enum OtelMetricType
    {
        /// <summary>
        /// Monotonically increasing counter.
        /// </summary>
        Counter,

        /// <summary>
        /// Point-in-time gauge value.
        /// </summary>
        Gauge,

        /// <summary>
        /// Distribution of values (histogram).
        /// </summary>
        Histogram
    }

    /// <summary>
    /// Represents histogram data for OpenTelemetry export.
    /// </summary>
    public sealed class OtelHistogramData
    {
        /// <summary>
        /// Gets or sets the count of observations.
        /// </summary>
        public long Count { get; set; }

        /// <summary>
        /// Gets or sets the sum of observations.
        /// </summary>
        public double Sum { get; set; }

        /// <summary>
        /// Gets or sets the minimum observed value.
        /// </summary>
        public double Min { get; set; }

        /// <summary>
        /// Gets or sets the maximum observed value.
        /// </summary>
        public double Max { get; set; }

        /// <summary>
        /// Gets or sets the percentile values.
        /// </summary>
        public Dictionary<double, double> Percentiles { get; set; } = new();

        /// <summary>
        /// Gets or sets the bucket counts (for explicit bucket histograms).
        /// </summary>
        public Dictionary<double, long>? BucketCounts { get; set; }
    }

    #endregion

    #region Tracing Adapter

    /// <summary>
    /// Adapts DataWarehouse operations to OpenTelemetry tracing using System.Diagnostics.Activity.
    /// Creates and manages spans/activities with proper context propagation.
    /// </summary>
    /// <example>
    /// <code>
    /// var tracingAdapter = new OpenTelemetryTracingAdapter();
    ///
    /// // Start a new span for an operation
    /// using (var scope = tracingAdapter.StartSpan("storage.read", SpanKind.Client))
    /// {
    ///     scope.SetAttribute("db.system", "datawarehouse");
    ///     scope.SetAttribute("db.operation", "read");
    ///
    ///     try
    ///     {
    ///         var result = await storage.ReadAsync(uri);
    ///         scope.SetStatus(SpanStatus.Ok);
    ///     }
    ///     catch (Exception ex)
    ///     {
    ///         scope.RecordException(ex);
    ///         scope.SetStatus(SpanStatus.Error, ex.Message);
    ///         throw;
    ///     }
    /// }
    ///
    /// // Propagate context through HTTP headers
    /// var headers = new Dictionary&lt;string, string&gt;();
    /// tracingAdapter.InjectContext(headers);
    /// // Send headers with HTTP request...
    ///
    /// // Extract context from incoming request
    /// var context = tracingAdapter.ExtractContext(incomingHeaders);
    /// using (var scope = tracingAdapter.StartSpan("handle.request", SpanKind.Server, context))
    /// {
    ///     // Handle request...
    /// }
    /// </code>
    /// </example>
    public sealed class OpenTelemetryTracingAdapter
    {
        private readonly ActivitySource _activitySource;
        private readonly OpenTelemetryTracingAdapterOptions _options;

        /// <summary>
        /// Gets the underlying ActivitySource.
        /// </summary>
        public ActivitySource ActivitySource => _activitySource;

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenTelemetryTracingAdapter"/> class.
        /// </summary>
        /// <param name="options">Configuration options (optional).</param>
        public OpenTelemetryTracingAdapter(OpenTelemetryTracingAdapterOptions? options = null)
        {
            _options = options ?? new OpenTelemetryTracingAdapterOptions();
            _activitySource = new ActivitySource(
                _options.SourceName ?? DataWarehouseInstrumentation.ServiceName,
                _options.SourceVersion ?? DataWarehouseInstrumentation.ServiceVersion);
        }

        /// <summary>
        /// Starts a new span/activity for an operation.
        /// </summary>
        /// <param name="operationName">The name of the operation.</param>
        /// <param name="kind">The span kind.</param>
        /// <param name="parentContext">Optional parent context for distributed tracing.</param>
        /// <param name="attributes">Optional initial attributes.</param>
        /// <param name="links">Optional links to related spans.</param>
        /// <returns>A scope that completes the span when disposed.</returns>
        public ITracingScope StartSpan(
            string operationName,
            SpanKind kind = SpanKind.Internal,
            PropagationContext? parentContext = null,
            IEnumerable<KeyValuePair<string, object?>>? attributes = null,
            IEnumerable<ActivityLink>? links = null)
        {
            ArgumentNullException.ThrowIfNull(operationName);

            var activityKind = ConvertSpanKind(kind);

            ActivityContext? activityContext = null;
            if (parentContext != null)
            {
                activityContext = new ActivityContext(
                    ActivityTraceId.CreateFromString(parentContext.TraceId),
                    ActivitySpanId.CreateFromString(parentContext.SpanId),
                    parentContext.TraceFlags);
            }

            var activity = _activitySource.StartActivity(
                operationName,
                activityKind,
                activityContext ?? default,
                attributes,
                links);

            if (activity == null)
            {
                // No listeners registered, return a no-op scope
                return NoOpTracingScope.Instance;
            }

            // Add default attributes
            foreach (var attr in _options.DefaultAttributes)
            {
                activity.SetTag(attr.Key, attr.Value);
            }

            return new ActivityTracingScope(activity);
        }

        /// <summary>
        /// Injects the current trace context into a carrier for propagation.
        /// Uses W3C Trace Context format (traceparent/tracestate headers).
        /// </summary>
        /// <param name="carrier">The carrier to inject context into (e.g., HTTP headers).</param>
        public void InjectContext(IDictionary<string, string> carrier)
        {
            ArgumentNullException.ThrowIfNull(carrier);

            var activity = Activity.Current;
            if (activity == null)
            {
                return;
            }

            // W3C Trace Context format
            var traceparent = $"00-{activity.TraceId}-{activity.SpanId}-{(activity.Recorded ? "01" : "00")}";
            carrier["traceparent"] = traceparent;

            if (!string.IsNullOrEmpty(activity.TraceStateString))
            {
                carrier["tracestate"] = activity.TraceStateString;
            }

            // Also add baggage
            foreach (var item in activity.Baggage)
            {
                carrier[$"baggage-{item.Key}"] = item.Value ?? string.Empty;
            }
        }

        /// <summary>
        /// Extracts trace context from a carrier.
        /// </summary>
        /// <param name="carrier">The carrier to extract context from (e.g., HTTP headers).</param>
        /// <returns>The extracted propagation context, or null if not present.</returns>
        public PropagationContext? ExtractContext(IDictionary<string, string> carrier)
        {
            ArgumentNullException.ThrowIfNull(carrier);

            if (!carrier.TryGetValue("traceparent", out var traceparent))
            {
                return null;
            }

            // Parse W3C Trace Context: 00-traceId-spanId-flags
            var parts = traceparent.Split('-');
            if (parts.Length < 4)
            {
                return null;
            }

            var context = new PropagationContext
            {
                TraceId = parts[1],
                SpanId = parts[2],
                TraceFlags = parts[3] == "01" ? ActivityTraceFlags.Recorded : ActivityTraceFlags.None
            };

            if (carrier.TryGetValue("tracestate", out var tracestate))
            {
                context.TraceState = tracestate;
            }

            // Extract baggage
            foreach (var kvp in carrier)
            {
                if (kvp.Key.StartsWith("baggage-", StringComparison.OrdinalIgnoreCase))
                {
                    var key = kvp.Key.Substring(8);
                    context.Baggage[key] = kvp.Value;
                }
            }

            return context;
        }

        /// <summary>
        /// Gets the current trace context.
        /// </summary>
        /// <returns>The current propagation context, or null if no active trace.</returns>
        public PropagationContext? GetCurrentContext()
        {
            var activity = Activity.Current;
            if (activity == null)
            {
                return null;
            }

            var context = new PropagationContext
            {
                TraceId = activity.TraceId.ToString(),
                SpanId = activity.SpanId.ToString(),
                TraceFlags = activity.Recorded ? ActivityTraceFlags.Recorded : ActivityTraceFlags.None,
                TraceState = activity.TraceStateString
            };

            foreach (var item in activity.Baggage)
            {
                context.Baggage[item.Key] = item.Value ?? string.Empty;
            }

            return context;
        }

        /// <summary>
        /// Converts an Activity to a TraceSpan for export.
        /// </summary>
        /// <param name="activity">The activity to convert.</param>
        /// <returns>A TraceSpan representation.</returns>
        public static TraceSpan ConvertToTraceSpan(Activity activity)
        {
            ArgumentNullException.ThrowIfNull(activity);

            var span = new TraceSpan
            {
                TraceId = activity.TraceId.ToString(),
                SpanId = activity.SpanId.ToString(),
                ParentSpanId = activity.ParentSpanId.ToString(),
                OperationName = activity.OperationName,
                Kind = ConvertActivityKind(activity.Kind),
                StartTime = activity.StartTimeUtc,
                EndTime = activity.StartTimeUtc + activity.Duration,
                Status = ConvertActivityStatus(activity.Status),
                StatusDescription = activity.StatusDescription
            };

            foreach (var tag in activity.Tags)
            {
                span.Attributes[tag.Key] = tag.Value ?? string.Empty;
            }

            foreach (var evt in activity.Events)
            {
                span.Events.Add(new SpanEvent
                {
                    Name = evt.Name,
                    Timestamp = evt.Timestamp,
                    Attributes = evt.Tags.ToDictionary(
                        t => t.Key,
                        t => (object)(t.Value ?? string.Empty))
                });
            }

            foreach (var link in activity.Links)
            {
                span.Links.Add(new SpanLink
                {
                    TraceId = link.Context.TraceId.ToString(),
                    SpanId = link.Context.SpanId.ToString(),
                    Attributes = link.Tags?.ToDictionary(
                        t => t.Key,
                        t => (object)(t.Value ?? string.Empty)) ?? new Dictionary<string, object>()
                });
            }

            return span;
        }

        private static ActivityKind ConvertSpanKind(SpanKind kind) => kind switch
        {
            SpanKind.Server => ActivityKind.Server,
            SpanKind.Client => ActivityKind.Client,
            SpanKind.Producer => ActivityKind.Producer,
            SpanKind.Consumer => ActivityKind.Consumer,
            _ => ActivityKind.Internal
        };

        private static SpanKind ConvertActivityKind(ActivityKind kind) => kind switch
        {
            ActivityKind.Server => SpanKind.Server,
            ActivityKind.Client => SpanKind.Client,
            ActivityKind.Producer => SpanKind.Producer,
            ActivityKind.Consumer => SpanKind.Consumer,
            _ => SpanKind.Internal
        };

        private static SpanStatus ConvertActivityStatus(ActivityStatusCode status) => status switch
        {
            ActivityStatusCode.Ok => SpanStatus.Ok,
            ActivityStatusCode.Error => SpanStatus.Error,
            _ => SpanStatus.Unset
        };
    }

    /// <summary>
    /// Configuration options for the OpenTelemetry tracing adapter.
    /// </summary>
    public sealed class OpenTelemetryTracingAdapterOptions
    {
        /// <summary>
        /// Gets or sets the ActivitySource name.
        /// </summary>
        public string? SourceName { get; set; }

        /// <summary>
        /// Gets or sets the ActivitySource version.
        /// </summary>
        public string? SourceVersion { get; set; }

        /// <summary>
        /// Gets or sets default attributes to add to all spans.
        /// </summary>
        public Dictionary<string, object> DefaultAttributes { get; set; } = new();

        /// <summary>
        /// Gets or sets whether to record exceptions as span events.
        /// </summary>
        public bool RecordExceptions { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum number of attributes per span.
        /// </summary>
        public int MaxAttributes { get; set; } = 128;

        /// <summary>
        /// Gets or sets the maximum number of events per span.
        /// </summary>
        public int MaxEvents { get; set; } = 128;

        /// <summary>
        /// Gets or sets the maximum number of links per span.
        /// </summary>
        public int MaxLinks { get; set; } = 128;
    }

    /// <summary>
    /// Represents trace context for propagation across service boundaries.
    /// </summary>
    public sealed class PropagationContext
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
        /// Gets or sets the trace flags.
        /// </summary>
        public ActivityTraceFlags TraceFlags { get; set; }

        /// <summary>
        /// Gets or sets the trace state string.
        /// </summary>
        public string? TraceState { get; set; }

        /// <summary>
        /// Gets or sets baggage items for propagation.
        /// </summary>
        public Dictionary<string, string> Baggage { get; set; } = new();
    }

    /// <summary>
    /// Defines a scope for a tracing span.
    /// </summary>
    public interface ITracingScope : IDisposable
    {
        /// <summary>
        /// Gets the trace ID of this scope.
        /// </summary>
        string TraceId { get; }

        /// <summary>
        /// Gets the span ID of this scope.
        /// </summary>
        string SpanId { get; }

        /// <summary>
        /// Sets an attribute on the span.
        /// </summary>
        /// <param name="key">The attribute key.</param>
        /// <param name="value">The attribute value.</param>
        void SetAttribute(string key, object? value);

        /// <summary>
        /// Sets the span status.
        /// </summary>
        /// <param name="status">The status.</param>
        /// <param name="description">Optional description (typically for errors).</param>
        void SetStatus(SpanStatus status, string? description = null);

        /// <summary>
        /// Records an exception on the span.
        /// </summary>
        /// <param name="exception">The exception to record.</param>
        void RecordException(Exception exception);

        /// <summary>
        /// Adds an event to the span.
        /// </summary>
        /// <param name="name">The event name.</param>
        /// <param name="attributes">Optional event attributes.</param>
        void AddEvent(string name, IDictionary<string, object?>? attributes = null);

        /// <summary>
        /// Sets a baggage item for propagation to child spans.
        /// </summary>
        /// <param name="key">The baggage key.</param>
        /// <param name="value">The baggage value.</param>
        void SetBaggage(string key, string value);
    }

    /// <summary>
    /// Tracing scope implementation backed by System.Diagnostics.Activity.
    /// </summary>
    internal sealed class ActivityTracingScope : ITracingScope
    {
        private readonly Activity _activity;
        private bool _disposed;

        public string TraceId => _activity.TraceId.ToString();
        public string SpanId => _activity.SpanId.ToString();

        public ActivityTracingScope(Activity activity)
        {
            _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        }

        public void SetAttribute(string key, object? value)
        {
            ThrowIfDisposed();
            _activity.SetTag(key, value);
        }

        public void SetStatus(SpanStatus status, string? description = null)
        {
            ThrowIfDisposed();

            var activityStatus = status switch
            {
                SpanStatus.Ok => ActivityStatusCode.Ok,
                SpanStatus.Error => ActivityStatusCode.Error,
                _ => ActivityStatusCode.Unset
            };

            _activity.SetStatus(activityStatus, description);
        }

        public void RecordException(Exception exception)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(exception);

            var tags = new ActivityTagsCollection
            {
                { "exception.type", exception.GetType().FullName },
                { "exception.message", exception.Message },
                { "exception.stacktrace", exception.StackTrace }
            };

            _activity.AddEvent(new ActivityEvent("exception", DateTimeOffset.UtcNow, tags));
        }

        public void AddEvent(string name, IDictionary<string, object?>? attributes = null)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(name);

            var tags = new ActivityTagsCollection();
            if (attributes != null)
            {
                foreach (var kvp in attributes)
                {
                    tags.Add(kvp.Key, kvp.Value);
                }
            }

            _activity.AddEvent(new ActivityEvent(name, DateTimeOffset.UtcNow, tags));
        }

        public void SetBaggage(string key, string value)
        {
            ThrowIfDisposed();
            _activity.SetBaggage(key, value);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activity.Dispose();
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    /// <summary>
    /// No-op tracing scope when no activity listener is registered.
    /// </summary>
    internal sealed class NoOpTracingScope : ITracingScope
    {
        public static readonly NoOpTracingScope Instance = new();

        public string TraceId => string.Empty;
        public string SpanId => string.Empty;

        private NoOpTracingScope() { }

        public void SetAttribute(string key, object? value) { }
        public void SetStatus(SpanStatus status, string? description = null) { }
        public void RecordException(Exception exception) { }
        public void AddEvent(string name, IDictionary<string, object?>? attributes = null) { }
        public void SetBaggage(string key, string value) { }
        public void Dispose() { }
    }

    #endregion

    #region Logging Adapter

    /// <summary>
    /// Adapts the DataWarehouse IStructuredLogger interface to OpenTelemetry LogRecord format.
    /// Includes trace correlation (TraceId, SpanId) and log severity mapping.
    /// </summary>
    /// <example>
    /// <code>
    /// var loggingAdapter = new OpenTelemetryLoggingAdapter(structuredLogger);
    ///
    /// // Log entries are automatically correlated with active traces
    /// using (var scope = tracingAdapter.StartSpan("process.request"))
    /// {
    ///     // This log entry will have TraceId and SpanId automatically attached
    ///     loggingAdapter.Log(LogLevel.Information, "Processing request", new Dictionary&lt;string, object&gt;
    ///     {
    ///         ["requestId"] = "12345",
    ///         ["userId"] = "user-abc"
    ///     });
    /// }
    ///
    /// // Convert log entries for OTLP export
    /// var otelLogs = loggingAdapter.ConvertToOtelLogRecords(logEntries);
    /// await exporter.ExportLogsAsync(logEntries);
    /// </code>
    /// </example>
    public sealed class OpenTelemetryLoggingAdapter : IStructuredLogger
    {
        private readonly IStructuredLogger _innerLogger;
        private readonly OpenTelemetryLoggingAdapterOptions _options;
        private readonly ConcurrentQueue<OtelLogRecord> _pendingLogs;
        private readonly string? _sourceContext;
        private readonly IReadOnlyDictionary<string, object> _contextProperties;

        /// <inheritdoc />
        public LogLevel MinimumLevel => _innerLogger.MinimumLevel;

        /// <summary>
        /// Gets the pending log records for batch export.
        /// </summary>
        public IEnumerable<OtelLogRecord> PendingLogs => _pendingLogs.ToArray();

        /// <summary>
        /// Gets the count of pending log records.
        /// </summary>
        public int PendingCount => _pendingLogs.Count;

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenTelemetryLoggingAdapter"/> class.
        /// </summary>
        /// <param name="innerLogger">The underlying structured logger.</param>
        /// <param name="options">Configuration options (optional).</param>
        public OpenTelemetryLoggingAdapter(
            IStructuredLogger innerLogger,
            OpenTelemetryLoggingAdapterOptions? options = null)
            : this(innerLogger, options, null, new Dictionary<string, object>())
        {
        }

        private OpenTelemetryLoggingAdapter(
            IStructuredLogger innerLogger,
            OpenTelemetryLoggingAdapterOptions? options,
            string? sourceContext,
            IReadOnlyDictionary<string, object> contextProperties)
        {
            _innerLogger = innerLogger ?? throw new ArgumentNullException(nameof(innerLogger));
            _options = options ?? new OpenTelemetryLoggingAdapterOptions();
            _pendingLogs = new ConcurrentQueue<OtelLogRecord>();
            _sourceContext = sourceContext;
            _contextProperties = contextProperties;
        }

        /// <inheritdoc />
        public void Log(LogLevel level, string message, Exception? exception = null, IReadOnlyDictionary<string, object>? properties = null)
        {
            if (!IsEnabled(level))
            {
                return;
            }

            // Forward to inner logger
            _innerLogger.Log(level, message, exception, properties);

            // Create OTel log record if buffering is enabled
            if (_options.BufferLogsForExport)
            {
                var record = CreateLogRecord(level, message, exception, properties);
                EnqueueLogRecord(record);
            }
        }

        /// <inheritdoc />
        public void Log(LogLevel level, string messageTemplate, params object[] args)
        {
            if (!IsEnabled(level))
            {
                return;
            }

            var message = args.Length > 0 ? string.Format(messageTemplate, args) : messageTemplate;
            Log(level, message);
        }

        /// <inheritdoc />
        public bool IsEnabled(LogLevel level)
        {
            return _innerLogger.IsEnabled(level);
        }

        /// <inheritdoc />
        public IStructuredLogger WithProperties(IReadOnlyDictionary<string, object> properties)
        {
            var merged = new Dictionary<string, object>(_contextProperties);
            foreach (var kvp in properties)
            {
                merged[kvp.Key] = kvp.Value;
            }

            return new OpenTelemetryLoggingAdapter(
                _innerLogger.WithProperties(properties),
                _options,
                _sourceContext,
                merged);
        }

        /// <inheritdoc />
        public IStructuredLogger ForContext(string sourceContext)
        {
            return new OpenTelemetryLoggingAdapter(
                _innerLogger.ForContext(sourceContext),
                _options,
                sourceContext,
                _contextProperties);
        }

        /// <inheritdoc />
        public IStructuredLogger ForContext<T>()
        {
            return ForContext(typeof(T).FullName ?? typeof(T).Name);
        }

        /// <summary>
        /// Converts log entries to OpenTelemetry log records for export.
        /// </summary>
        /// <param name="entries">The log entries to convert.</param>
        /// <returns>A collection of OTel log records.</returns>
        public IEnumerable<OtelLogRecord> ConvertToOtelLogRecords(IEnumerable<LogEntry> entries)
        {
            ArgumentNullException.ThrowIfNull(entries);

            foreach (var entry in entries)
            {
                yield return CreateLogRecord(
                    entry.Level,
                    entry.Message,
                    entry.Exception,
                    entry.Properties);
            }
        }

        /// <summary>
        /// Drains and returns all pending log records.
        /// </summary>
        /// <returns>All pending log records.</returns>
        public OtelLogRecord[] DrainPendingLogs()
        {
            var records = new List<OtelLogRecord>();

            while (_pendingLogs.TryDequeue(out var record))
            {
                records.Add(record);
            }

            return records.ToArray();
        }

        /// <summary>
        /// Clears all pending log records.
        /// </summary>
        public void ClearPendingLogs()
        {
            while (_pendingLogs.TryDequeue(out _)) { }
        }

        private OtelLogRecord CreateLogRecord(
            LogLevel level,
            string message,
            Exception? exception,
            IReadOnlyDictionary<string, object>? properties)
        {
            var activity = Activity.Current;

            var record = new OtelLogRecord
            {
                Timestamp = DateTimeOffset.UtcNow,
                SeverityNumber = MapToOtelSeverity(level),
                SeverityText = level.ToString(),
                Body = message,
                TraceId = activity?.TraceId.ToString(),
                SpanId = activity?.SpanId.ToString(),
                TraceFlags = activity?.Recorded == true ? "01" : "00",
                ServiceName = _options.ServiceName ?? DataWarehouseInstrumentation.ServiceName,
                SourceContext = _sourceContext
            };

            // Add context properties
            foreach (var kvp in _contextProperties)
            {
                record.Attributes[kvp.Key] = kvp.Value;
            }

            // Add log properties
            if (properties != null)
            {
                foreach (var kvp in properties)
                {
                    record.Attributes[kvp.Key] = kvp.Value;
                }
            }

            // Add exception details
            if (exception != null)
            {
                record.Attributes["exception.type"] = exception.GetType().FullName ?? exception.GetType().Name;
                record.Attributes["exception.message"] = exception.Message;
                record.Attributes["exception.stacktrace"] = exception.StackTrace ?? string.Empty;

                if (exception.InnerException != null)
                {
                    record.Attributes["exception.inner.type"] = exception.InnerException.GetType().FullName ?? exception.InnerException.GetType().Name;
                    record.Attributes["exception.inner.message"] = exception.InnerException.Message;
                }
            }

            // Add default attributes
            foreach (var kvp in _options.DefaultAttributes)
            {
                if (!record.Attributes.ContainsKey(kvp.Key))
                {
                    record.Attributes[kvp.Key] = kvp.Value;
                }
            }

            return record;
        }

        private void EnqueueLogRecord(OtelLogRecord record)
        {
            _pendingLogs.Enqueue(record);

            // Enforce max queue size
            while (_pendingLogs.Count > _options.MaxBufferedLogs)
            {
                _pendingLogs.TryDequeue(out _);
            }
        }

        /// <summary>
        /// Maps DataWarehouse LogLevel to OpenTelemetry severity number.
        /// Based on OpenTelemetry Log Data Model specification.
        /// </summary>
        private static int MapToOtelSeverity(LogLevel level) => level switch
        {
            LogLevel.Trace => 1,        // TRACE
            LogLevel.Debug => 5,        // DEBUG
            LogLevel.Information => 9,  // INFO
            LogLevel.Warning => 13,     // WARN
            LogLevel.Error => 17,       // ERROR
            LogLevel.Critical => 21,    // FATAL
            _ => 0                      // UNSPECIFIED
        };
    }

    /// <summary>
    /// Configuration options for the OpenTelemetry logging adapter.
    /// </summary>
    public sealed class OpenTelemetryLoggingAdapterOptions
    {
        /// <summary>
        /// Gets or sets the service name for log records.
        /// </summary>
        public string? ServiceName { get; set; }

        /// <summary>
        /// Gets or sets whether to buffer logs for batch export.
        /// </summary>
        public bool BufferLogsForExport { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum number of buffered log records.
        /// </summary>
        public int MaxBufferedLogs { get; set; } = 10000;

        /// <summary>
        /// Gets or sets default attributes to add to all log records.
        /// </summary>
        public Dictionary<string, object> DefaultAttributes { get; set; } = new();

        /// <summary>
        /// Gets or sets whether to include exception stack traces.
        /// </summary>
        public bool IncludeStackTraces { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum stack trace length.
        /// </summary>
        public int MaxStackTraceLength { get; set; } = 4096;
    }

    /// <summary>
    /// Represents an OpenTelemetry-compatible log record.
    /// </summary>
    public sealed class OtelLogRecord
    {
        /// <summary>
        /// Gets or sets the log timestamp.
        /// </summary>
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the observed timestamp (when the log was collected).
        /// </summary>
        public DateTimeOffset ObservedTimestamp { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Gets or sets the severity number (1-24 per OTel spec).
        /// </summary>
        public int SeverityNumber { get; set; }

        /// <summary>
        /// Gets or sets the severity text.
        /// </summary>
        public string SeverityText { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the log body/message.
        /// </summary>
        public string Body { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the trace ID for correlation.
        /// </summary>
        public string? TraceId { get; set; }

        /// <summary>
        /// Gets or sets the span ID for correlation.
        /// </summary>
        public string? SpanId { get; set; }

        /// <summary>
        /// Gets or sets the trace flags.
        /// </summary>
        public string? TraceFlags { get; set; }

        /// <summary>
        /// Gets or sets the service name.
        /// </summary>
        public string ServiceName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the source context (logger name).
        /// </summary>
        public string? SourceContext { get; set; }

        /// <summary>
        /// Gets or sets the log attributes.
        /// </summary>
        public Dictionary<string, object> Attributes { get; set; } = new();

        /// <summary>
        /// Gets or sets the resource attributes.
        /// </summary>
        public Dictionary<string, object> ResourceAttributes { get; set; } = new();

        /// <summary>
        /// Gets or sets the instrumentation scope name.
        /// </summary>
        public string InstrumentationScopeName { get; set; } = DataWarehouseInstrumentation.ServiceName;

        /// <summary>
        /// Gets or sets the instrumentation scope version.
        /// </summary>
        public string? InstrumentationScopeVersion { get; set; }
    }

    #endregion

    #region DataWarehouse Instrumentation

    /// <summary>
    /// Provides pre-configured instrumentation for DataWarehouse operations.
    /// Contains ActivitySource for tracing and Meter for metrics with semantic conventions.
    /// </summary>
    /// <example>
    /// <code>
    /// // Start a storage operation span
    /// using var activity = DataWarehouseInstrumentation.StartStorageActivity("read", uri);
    /// if (activity != null)
    /// {
    ///     activity.SetTag(DataWarehouseInstrumentation.Tags.StorageUri, uri.ToString());
    ///     activity.SetTag(DataWarehouseInstrumentation.Tags.StorageOperation, "read");
    /// }
    ///
    /// // Record a storage metric
    /// DataWarehouseInstrumentation.RecordStorageLatency(150.5, "read", "blob");
    /// DataWarehouseInstrumentation.IncrementStorageOperations("read", "blob", success: true);
    ///
    /// // Use pre-defined counters
    /// DataWarehouseInstrumentation.PipelineOperationsCounter.Add(1,
    ///     new KeyValuePair&lt;string, object?&gt;("pipeline.stage", "transform"));
    /// </code>
    /// </example>
    public static class DataWarehouseInstrumentation
    {
        /// <summary>
        /// The service name for DataWarehouse instrumentation.
        /// </summary>
        public const string ServiceName = "DataWarehouse";

        /// <summary>
        /// The service version for DataWarehouse instrumentation.
        /// </summary>
        public const string ServiceVersion = "1.0.0";

        /// <summary>
        /// Gets the ActivitySource for DataWarehouse tracing.
        /// </summary>
        public static ActivitySource ActivitySource { get; } = new(ServiceName, ServiceVersion);

        /// <summary>
        /// Gets the Meter for DataWarehouse metrics.
        /// </summary>
        public static Meter Meter { get; } = new(ServiceName, ServiceVersion);

        #region Pre-defined Instruments

        /// <summary>
        /// Counter for storage operations.
        /// </summary>
        public static Counter<long> StorageOperationsCounter { get; } = Meter.CreateCounter<long>(
            "datawarehouse.storage.operations",
            unit: "{operations}",
            description: "Number of storage operations performed");

        /// <summary>
        /// Histogram for storage operation latency.
        /// </summary>
        public static Histogram<double> StorageLatencyHistogram { get; } = Meter.CreateHistogram<double>(
            "datawarehouse.storage.latency",
            unit: "ms",
            description: "Latency of storage operations in milliseconds");

        /// <summary>
        /// Counter for storage bytes read/written.
        /// </summary>
        public static Counter<long> StorageBytesCounter { get; } = Meter.CreateCounter<long>(
            "datawarehouse.storage.bytes",
            unit: "By",
            description: "Number of bytes transferred in storage operations");

        /// <summary>
        /// Counter for pipeline operations.
        /// </summary>
        public static Counter<long> PipelineOperationsCounter { get; } = Meter.CreateCounter<long>(
            "datawarehouse.pipeline.operations",
            unit: "{operations}",
            description: "Number of pipeline operations performed");

        /// <summary>
        /// Histogram for pipeline stage latency.
        /// </summary>
        public static Histogram<double> PipelineStageLatencyHistogram { get; } = Meter.CreateHistogram<double>(
            "datawarehouse.pipeline.stage.latency",
            unit: "ms",
            description: "Latency of pipeline stages in milliseconds");

        /// <summary>
        /// Counter for plugin invocations.
        /// </summary>
        public static Counter<long> PluginInvocationsCounter { get; } = Meter.CreateCounter<long>(
            "datawarehouse.plugin.invocations",
            unit: "{invocations}",
            description: "Number of plugin invocations");

        /// <summary>
        /// Counter for message bus messages.
        /// </summary>
        public static Counter<long> MessageBusMessagesCounter { get; } = Meter.CreateCounter<long>(
            "datawarehouse.messagebus.messages",
            unit: "{messages}",
            description: "Number of messages processed by the message bus");

        /// <summary>
        /// Histogram for query execution time.
        /// </summary>
        public static Histogram<double> QueryLatencyHistogram { get; } = Meter.CreateHistogram<double>(
            "datawarehouse.query.latency",
            unit: "ms",
            description: "Latency of query operations in milliseconds");

        /// <summary>
        /// Counter for errors.
        /// </summary>
        public static Counter<long> ErrorsCounter { get; } = Meter.CreateCounter<long>(
            "datawarehouse.errors",
            unit: "{errors}",
            description: "Number of errors occurred");

        #endregion

        #region Semantic Convention Tags

        /// <summary>
        /// Semantic convention tag names for DataWarehouse operations.
        /// </summary>
        public static class Tags
        {
            /// <summary>Storage URI.</summary>
            public const string StorageUri = "datawarehouse.storage.uri";

            /// <summary>Storage operation type (read, write, delete, list).</summary>
            public const string StorageOperation = "datawarehouse.storage.operation";

            /// <summary>Storage provider type (blob, file, memory).</summary>
            public const string StorageProvider = "datawarehouse.storage.provider";

            /// <summary>Storage tier (hot, cold, archive).</summary>
            public const string StorageTier = "datawarehouse.storage.tier";

            /// <summary>Pipeline name.</summary>
            public const string PipelineName = "datawarehouse.pipeline.name";

            /// <summary>Pipeline stage name.</summary>
            public const string PipelineStage = "datawarehouse.pipeline.stage";

            /// <summary>Pipeline stage index.</summary>
            public const string PipelineStageIndex = "datawarehouse.pipeline.stage.index";

            /// <summary>Plugin ID.</summary>
            public const string PluginId = "datawarehouse.plugin.id";

            /// <summary>Plugin type.</summary>
            public const string PluginType = "datawarehouse.plugin.type";

            /// <summary>Plugin version.</summary>
            public const string PluginVersion = "datawarehouse.plugin.version";

            /// <summary>Query type.</summary>
            public const string QueryType = "datawarehouse.query.type";

            /// <summary>Query result count.</summary>
            public const string QueryResultCount = "datawarehouse.query.result_count";

            /// <summary>Error type.</summary>
            public const string ErrorType = "datawarehouse.error.type";

            /// <summary>Error code.</summary>
            public const string ErrorCode = "datawarehouse.error.code";

            /// <summary>Operation success flag.</summary>
            public const string OperationSuccess = "datawarehouse.operation.success";

            /// <summary>Correlation ID for distributed tracing.</summary>
            public const string CorrelationId = "datawarehouse.correlation_id";

            /// <summary>User ID for auditing.</summary>
            public const string UserId = "datawarehouse.user_id";

            /// <summary>Tenant ID for multi-tenant scenarios.</summary>
            public const string TenantId = "datawarehouse.tenant_id";
        }

        #endregion

        #region Activity Helpers

        /// <summary>
        /// Starts an activity for a storage operation.
        /// </summary>
        /// <param name="operation">The operation type (read, write, delete, list).</param>
        /// <param name="uri">The storage URI.</param>
        /// <param name="provider">Optional storage provider name.</param>
        /// <returns>The started activity, or null if no listeners.</returns>
        public static Activity? StartStorageActivity(string operation, Uri? uri = null, string? provider = null)
        {
            var activity = ActivitySource.StartActivity(
                $"storage.{operation}",
                ActivityKind.Client);

            if (activity != null)
            {
                activity.SetTag(Tags.StorageOperation, operation);

                if (uri != null)
                {
                    activity.SetTag(Tags.StorageUri, uri.ToString());
                }

                if (provider != null)
                {
                    activity.SetTag(Tags.StorageProvider, provider);
                }
            }

            return activity;
        }

        /// <summary>
        /// Starts an activity for a pipeline operation.
        /// </summary>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="stageName">Optional stage name.</param>
        /// <param name="stageIndex">Optional stage index.</param>
        /// <returns>The started activity, or null if no listeners.</returns>
        public static Activity? StartPipelineActivity(string pipelineName, string? stageName = null, int? stageIndex = null)
        {
            var operationName = stageName != null
                ? $"pipeline.{pipelineName}.{stageName}"
                : $"pipeline.{pipelineName}";

            var activity = ActivitySource.StartActivity(operationName, ActivityKind.Internal);

            if (activity != null)
            {
                activity.SetTag(Tags.PipelineName, pipelineName);

                if (stageName != null)
                {
                    activity.SetTag(Tags.PipelineStage, stageName);
                }

                if (stageIndex.HasValue)
                {
                    activity.SetTag(Tags.PipelineStageIndex, stageIndex.Value);
                }
            }

            return activity;
        }

        /// <summary>
        /// Starts an activity for a plugin invocation.
        /// </summary>
        /// <param name="pluginId">The plugin ID.</param>
        /// <param name="pluginType">Optional plugin type.</param>
        /// <returns>The started activity, or null if no listeners.</returns>
        public static Activity? StartPluginActivity(string pluginId, string? pluginType = null)
        {
            var activity = ActivitySource.StartActivity($"plugin.{pluginId}", ActivityKind.Internal);

            if (activity != null)
            {
                activity.SetTag(Tags.PluginId, pluginId);

                if (pluginType != null)
                {
                    activity.SetTag(Tags.PluginType, pluginType);
                }
            }

            return activity;
        }

        /// <summary>
        /// Starts an activity for a query operation.
        /// </summary>
        /// <param name="queryType">The query type.</param>
        /// <returns>The started activity, or null if no listeners.</returns>
        public static Activity? StartQueryActivity(string queryType)
        {
            var activity = ActivitySource.StartActivity($"query.{queryType}", ActivityKind.Internal);

            if (activity != null)
            {
                activity.SetTag(Tags.QueryType, queryType);
            }

            return activity;
        }

        #endregion

        #region Metric Helpers

        /// <summary>
        /// Records storage operation latency.
        /// </summary>
        /// <param name="latencyMs">Latency in milliseconds.</param>
        /// <param name="operation">Operation type.</param>
        /// <param name="provider">Storage provider.</param>
        /// <param name="success">Whether the operation succeeded.</param>
        public static void RecordStorageLatency(double latencyMs, string operation, string? provider = null, bool success = true)
        {
            var tags = new TagList
            {
                { Tags.StorageOperation, operation },
                { Tags.OperationSuccess, success }
            };

            if (provider != null)
            {
                tags.Add(Tags.StorageProvider, provider);
            }

            StorageLatencyHistogram.Record(latencyMs, tags);
        }

        /// <summary>
        /// Increments storage operations counter.
        /// </summary>
        /// <param name="operation">Operation type.</param>
        /// <param name="provider">Storage provider.</param>
        /// <param name="success">Whether the operation succeeded.</param>
        public static void IncrementStorageOperations(string operation, string? provider = null, bool success = true)
        {
            var tags = new TagList
            {
                { Tags.StorageOperation, operation },
                { Tags.OperationSuccess, success }
            };

            if (provider != null)
            {
                tags.Add(Tags.StorageProvider, provider);
            }

            StorageOperationsCounter.Add(1, tags);
        }

        /// <summary>
        /// Records storage bytes transferred.
        /// </summary>
        /// <param name="bytes">Number of bytes.</param>
        /// <param name="operation">Operation type (read/write).</param>
        /// <param name="provider">Storage provider.</param>
        public static void RecordStorageBytes(long bytes, string operation, string? provider = null)
        {
            var tags = new TagList
            {
                { Tags.StorageOperation, operation }
            };

            if (provider != null)
            {
                tags.Add(Tags.StorageProvider, provider);
            }

            StorageBytesCounter.Add(bytes, tags);
        }

        /// <summary>
        /// Records pipeline stage latency.
        /// </summary>
        /// <param name="latencyMs">Latency in milliseconds.</param>
        /// <param name="pipelineName">Pipeline name.</param>
        /// <param name="stageName">Stage name.</param>
        /// <param name="success">Whether the stage succeeded.</param>
        public static void RecordPipelineStageLatency(double latencyMs, string pipelineName, string stageName, bool success = true)
        {
            PipelineStageLatencyHistogram.Record(latencyMs, new TagList
            {
                { Tags.PipelineName, pipelineName },
                { Tags.PipelineStage, stageName },
                { Tags.OperationSuccess, success }
            });
        }

        /// <summary>
        /// Increments pipeline operations counter.
        /// </summary>
        /// <param name="pipelineName">Pipeline name.</param>
        /// <param name="stageName">Optional stage name.</param>
        /// <param name="success">Whether the operation succeeded.</param>
        public static void IncrementPipelineOperations(string pipelineName, string? stageName = null, bool success = true)
        {
            var tags = new TagList
            {
                { Tags.PipelineName, pipelineName },
                { Tags.OperationSuccess, success }
            };

            if (stageName != null)
            {
                tags.Add(Tags.PipelineStage, stageName);
            }

            PipelineOperationsCounter.Add(1, tags);
        }

        /// <summary>
        /// Increments plugin invocations counter.
        /// </summary>
        /// <param name="pluginId">Plugin ID.</param>
        /// <param name="pluginType">Optional plugin type.</param>
        /// <param name="success">Whether the invocation succeeded.</param>
        public static void IncrementPluginInvocations(string pluginId, string? pluginType = null, bool success = true)
        {
            var tags = new TagList
            {
                { Tags.PluginId, pluginId },
                { Tags.OperationSuccess, success }
            };

            if (pluginType != null)
            {
                tags.Add(Tags.PluginType, pluginType);
            }

            PluginInvocationsCounter.Add(1, tags);
        }

        /// <summary>
        /// Records query latency.
        /// </summary>
        /// <param name="latencyMs">Latency in milliseconds.</param>
        /// <param name="queryType">Query type.</param>
        /// <param name="resultCount">Optional result count.</param>
        /// <param name="success">Whether the query succeeded.</param>
        public static void RecordQueryLatency(double latencyMs, string queryType, int? resultCount = null, bool success = true)
        {
            var tags = new TagList
            {
                { Tags.QueryType, queryType },
                { Tags.OperationSuccess, success }
            };

            if (resultCount.HasValue)
            {
                tags.Add(Tags.QueryResultCount, resultCount.Value);
            }

            QueryLatencyHistogram.Record(latencyMs, tags);
        }

        /// <summary>
        /// Increments the error counter.
        /// </summary>
        /// <param name="errorType">The type of error.</param>
        /// <param name="errorCode">Optional error code.</param>
        public static void IncrementErrors(string errorType, string? errorCode = null)
        {
            var tags = new TagList
            {
                { Tags.ErrorType, errorType }
            };

            if (errorCode != null)
            {
                tags.Add(Tags.ErrorCode, errorCode);
            }

            ErrorsCounter.Add(1, tags);
        }

        #endregion
    }

    #endregion
}
