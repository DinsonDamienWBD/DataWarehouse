using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.SigNoz
{
    #region OTLP Protocol Types

    /// <summary>
    /// Represents an OTLP resource with attributes.
    /// </summary>
    public sealed class OtlpResource
    {
        [JsonPropertyName("attributes")]
        public List<OtlpKeyValue> Attributes { get; set; } = new();
    }

    /// <summary>
    /// Represents a key-value pair in OTLP format.
    /// </summary>
    public sealed class OtlpKeyValue
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public OtlpAnyValue Value { get; set; } = new();
    }

    /// <summary>
    /// Represents an OTLP value that can be of any type.
    /// </summary>
    public sealed class OtlpAnyValue
    {
        [JsonPropertyName("stringValue")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StringValue { get; set; }

        [JsonPropertyName("intValue")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? IntValue { get; set; }

        [JsonPropertyName("doubleValue")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? DoubleValue { get; set; }

        [JsonPropertyName("boolValue")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? BoolValue { get; set; }
    }

    #endregion

    #region OTLP Metrics

    /// <summary>
    /// Root OTLP metrics export request.
    /// </summary>
    public sealed class OtlpMetricsExportRequest
    {
        [JsonPropertyName("resourceMetrics")]
        public List<OtlpResourceMetrics> ResourceMetrics { get; set; } = new();
    }

    /// <summary>
    /// Resource-level metrics container.
    /// </summary>
    public sealed class OtlpResourceMetrics
    {
        [JsonPropertyName("resource")]
        public OtlpResource Resource { get; set; } = new();

        [JsonPropertyName("scopeMetrics")]
        public List<OtlpScopeMetrics> ScopeMetrics { get; set; } = new();
    }

    /// <summary>
    /// Scope-level metrics container.
    /// </summary>
    public sealed class OtlpScopeMetrics
    {
        [JsonPropertyName("scope")]
        public OtlpInstrumentationScope Scope { get; set; } = new();

        [JsonPropertyName("metrics")]
        public List<OtlpMetric> Metrics { get; set; } = new();
    }

    /// <summary>
    /// Instrumentation scope/library information.
    /// </summary>
    public sealed class OtlpInstrumentationScope
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Version { get; set; }
    }

    /// <summary>
    /// Individual OTLP metric.
    /// </summary>
    public sealed class OtlpMetric
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        [JsonPropertyName("unit")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Unit { get; set; }

        [JsonPropertyName("sum")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OtlpSum? Sum { get; set; }

        [JsonPropertyName("gauge")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OtlpGauge? Gauge { get; set; }

        [JsonPropertyName("histogram")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OtlpHistogram? Histogram { get; set; }
    }

    /// <summary>
    /// OTLP sum metric (for counters).
    /// </summary>
    public sealed class OtlpSum
    {
        [JsonPropertyName("dataPoints")]
        public List<OtlpNumberDataPoint> DataPoints { get; set; } = new();

        [JsonPropertyName("aggregationTemporality")]
        public int AggregationTemporality { get; set; } = 2; // CUMULATIVE

        [JsonPropertyName("isMonotonic")]
        public bool IsMonotonic { get; set; } = true;
    }

    /// <summary>
    /// OTLP gauge metric.
    /// </summary>
    public sealed class OtlpGauge
    {
        [JsonPropertyName("dataPoints")]
        public List<OtlpNumberDataPoint> DataPoints { get; set; } = new();
    }

    /// <summary>
    /// OTLP histogram metric.
    /// </summary>
    public sealed class OtlpHistogram
    {
        [JsonPropertyName("dataPoints")]
        public List<OtlpHistogramDataPoint> DataPoints { get; set; } = new();

        [JsonPropertyName("aggregationTemporality")]
        public int AggregationTemporality { get; set; } = 2; // CUMULATIVE
    }

    /// <summary>
    /// Number data point for sum/gauge metrics.
    /// </summary>
    public sealed class OtlpNumberDataPoint
    {
        [JsonPropertyName("attributes")]
        public List<OtlpKeyValue> Attributes { get; set; } = new();

        [JsonPropertyName("timeUnixNano")]
        public string TimeUnixNano { get; set; } = string.Empty;

        [JsonPropertyName("asDouble")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? AsDouble { get; set; }

        [JsonPropertyName("asInt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? AsInt { get; set; }
    }

    /// <summary>
    /// Histogram data point.
    /// </summary>
    public sealed class OtlpHistogramDataPoint
    {
        [JsonPropertyName("attributes")]
        public List<OtlpKeyValue> Attributes { get; set; } = new();

        [JsonPropertyName("timeUnixNano")]
        public string TimeUnixNano { get; set; } = string.Empty;

        [JsonPropertyName("count")]
        public ulong Count { get; set; }

        [JsonPropertyName("sum")]
        public double Sum { get; set; }

        [JsonPropertyName("bucketCounts")]
        public List<ulong> BucketCounts { get; set; } = new();

        [JsonPropertyName("explicitBounds")]
        public List<double> ExplicitBounds { get; set; } = new();

        [JsonPropertyName("min")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Min { get; set; }

        [JsonPropertyName("max")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Max { get; set; }
    }

    #endregion

    #region OTLP Logs

    /// <summary>
    /// Root OTLP logs export request.
    /// </summary>
    public sealed class OtlpLogsExportRequest
    {
        [JsonPropertyName("resourceLogs")]
        public List<OtlpResourceLogs> ResourceLogs { get; set; } = new();
    }

    /// <summary>
    /// Resource-level logs container.
    /// </summary>
    public sealed class OtlpResourceLogs
    {
        [JsonPropertyName("resource")]
        public OtlpResource Resource { get; set; } = new();

        [JsonPropertyName("scopeLogs")]
        public List<OtlpScopeLogs> ScopeLogs { get; set; } = new();
    }

    /// <summary>
    /// Scope-level logs container.
    /// </summary>
    public sealed class OtlpScopeLogs
    {
        [JsonPropertyName("scope")]
        public OtlpInstrumentationScope Scope { get; set; } = new();

        [JsonPropertyName("logRecords")]
        public List<OtlpLogRecord> LogRecords { get; set; } = new();
    }

    /// <summary>
    /// Individual OTLP log record.
    /// </summary>
    public sealed class OtlpLogRecord
    {
        [JsonPropertyName("timeUnixNano")]
        public string TimeUnixNano { get; set; } = string.Empty;

        [JsonPropertyName("severityNumber")]
        public int SeverityNumber { get; set; }

        [JsonPropertyName("severityText")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SeverityText { get; set; }

        [JsonPropertyName("body")]
        public OtlpAnyValue Body { get; set; } = new();

        [JsonPropertyName("attributes")]
        public List<OtlpKeyValue> Attributes { get; set; } = new();
    }

    #endregion

    #region OTLP Traces

    /// <summary>
    /// Root OTLP traces export request.
    /// </summary>
    public sealed class OtlpTracesExportRequest
    {
        [JsonPropertyName("resourceSpans")]
        public List<OtlpResourceSpans> ResourceSpans { get; set; } = new();
    }

    /// <summary>
    /// Resource-level spans container.
    /// </summary>
    public sealed class OtlpResourceSpans
    {
        [JsonPropertyName("resource")]
        public OtlpResource Resource { get; set; } = new();

        [JsonPropertyName("scopeSpans")]
        public List<OtlpScopeSpans> ScopeSpans { get; set; } = new();
    }

    /// <summary>
    /// Scope-level spans container.
    /// </summary>
    public sealed class OtlpScopeSpans
    {
        [JsonPropertyName("scope")]
        public OtlpInstrumentationScope Scope { get; set; } = new();

        [JsonPropertyName("spans")]
        public List<OtlpSpan> Spans { get; set; } = new();
    }

    /// <summary>
    /// Individual OTLP span (trace).
    /// </summary>
    public sealed class OtlpSpan
    {
        [JsonPropertyName("traceId")]
        public string TraceId { get; set; } = string.Empty;

        [JsonPropertyName("spanId")]
        public string SpanId { get; set; } = string.Empty;

        [JsonPropertyName("parentSpanId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ParentSpanId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("kind")]
        public int Kind { get; set; } = 1; // INTERNAL

        [JsonPropertyName("startTimeUnixNano")]
        public string StartTimeUnixNano { get; set; } = string.Empty;

        [JsonPropertyName("endTimeUnixNano")]
        public string EndTimeUnixNano { get; set; } = string.Empty;

        [JsonPropertyName("attributes")]
        public List<OtlpKeyValue> Attributes { get; set; } = new();

        [JsonPropertyName("status")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OtlpStatus? Status { get; set; }
    }

    /// <summary>
    /// Span status.
    /// </summary>
    public sealed class OtlpStatus
    {
        [JsonPropertyName("code")]
        public int Code { get; set; } = 1; // OK

        [JsonPropertyName("message")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Message { get; set; }
    }

    #endregion

    #region Configuration

    /// <summary>
    /// Configuration for the SigNoz plugin.
    /// </summary>
    public sealed class SigNozConfiguration
    {
        /// <summary>
        /// Gets or sets the SigNoz OTLP endpoint URL.
        /// </summary>
        public string SigNozUrl { get; set; } = "http://localhost:4318";

        /// <summary>
        /// Gets or sets the service name for identification.
        /// </summary>
        public string ServiceName { get; set; } = "datawarehouse";

        /// <summary>
        /// Gets or sets the service version.
        /// </summary>
        public string ServiceVersion { get; set; } = "1.0.0";

        /// <summary>
        /// Gets or sets custom headers for authentication.
        /// </summary>
        public Dictionary<string, string> Headers { get; set; } = new();

        /// <summary>
        /// Gets or sets the export interval for batching.
        /// </summary>
        public TimeSpan ExportInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets or sets the maximum batch size for metrics/logs/traces.
        /// </summary>
        public int MaxBatchSize { get; set; } = 100;

        /// <summary>
        /// Gets or sets whether to enable metrics export.
        /// </summary>
        public bool EnableMetrics { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable logs export.
        /// </summary>
        public bool EnableLogs { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable traces export.
        /// </summary>
        public bool EnableTraces { get; set; } = true;

        /// <summary>
        /// Gets or sets the HTTP timeout for requests.
        /// </summary>
        public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets environment name for resource attributes.
        /// </summary>
        public string? Environment { get; set; }

        /// <summary>
        /// Gets or sets deployment name for resource attributes.
        /// </summary>
        public string? Deployment { get; set; }
    }

    #endregion
}
