using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.Dynatrace
{
    #region Configuration

    /// <summary>
    /// Configuration options for the Dynatrace plugin.
    /// </summary>
    public sealed class DynatraceConfiguration
    {
        /// <summary>
        /// Gets or sets the Dynatrace environment URL (e.g., https://abc12345.live.dynatrace.com).
        /// </summary>
        public string DynatraceUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Dynatrace API token for authentication.
        /// </summary>
        public string ApiToken { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the metrics ingestion endpoint path.
        /// </summary>
        public string MetricsEndpoint { get; set; } = "/api/v2/metrics/ingest";

        /// <summary>
        /// Gets or sets the logs ingestion endpoint path.
        /// </summary>
        public string LogsEndpoint { get; set; } = "/api/v2/logs/ingest";

        /// <summary>
        /// Gets or sets the metric prefix for all metrics.
        /// </summary>
        public string MetricPrefix { get; set; } = "datawarehouse";

        /// <summary>
        /// Gets or sets the batch size for metrics ingestion.
        /// </summary>
        public int MetricsBatchSize { get; set; } = 100;

        /// <summary>
        /// Gets or sets the batch size for logs ingestion.
        /// </summary>
        public int LogsBatchSize { get; set; } = 100;

        /// <summary>
        /// Gets or sets the flush interval for batched metrics and logs.
        /// </summary>
        public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets or sets whether to validate SSL certificates.
        /// </summary>
        public bool ValidateSslCertificate { get; set; } = true;

        /// <summary>
        /// Gets or sets the HTTP timeout.
        /// </summary>
        public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets default dimensions to add to all metrics.
        /// </summary>
        public Dictionary<string, string>? DefaultDimensions { get; set; }

        /// <summary>
        /// Gets or sets default attributes to add to all logs.
        /// </summary>
        public Dictionary<string, string>? DefaultLogAttributes { get; set; }
    }

    #endregion

    #region Metric Types

    /// <summary>
    /// Represents a Dynatrace metric in line protocol format.
    /// </summary>
    public sealed class DynatraceMetric
    {
        /// <summary>
        /// Gets or sets the metric name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the metric value.
        /// </summary>
        public double Value { get; set; }

        /// <summary>
        /// Gets or sets the timestamp in milliseconds since epoch.
        /// </summary>
        public long Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the dimensions (key-value pairs).
        /// </summary>
        public Dictionary<string, string> Dimensions { get; set; } = new();

        /// <summary>
        /// Converts the metric to Dynatrace line protocol format.
        /// </summary>
        /// <returns>The metric in line protocol format.</returns>
        public string ToLineProtocol()
        {
            var sb = new StringBuilder();
            sb.Append(EscapeMetricName(Name));

            if (Dimensions.Count > 0)
            {
                foreach (var dim in Dimensions)
                {
                    sb.Append(',');
                    sb.Append(EscapeKey(dim.Key));
                    sb.Append('=');
                    sb.Append(EscapeValue(dim.Value));
                }
            }

            sb.Append(' ');

            if (double.IsNaN(Value) || double.IsInfinity(Value))
            {
                sb.Append('0');
            }
            else
            {
                sb.Append(Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            sb.Append(' ');
            sb.Append(Timestamp);

            return sb.ToString();
        }

        private static string EscapeMetricName(string name)
        {
            return name.Replace(",", "\\,")
                       .Replace(" ", "\\ ")
                       .Replace("=", "\\=");
        }

        private static string EscapeKey(string key)
        {
            return key.Replace(",", "\\,")
                      .Replace(" ", "\\ ")
                      .Replace("=", "\\=");
        }

        private static string EscapeValue(string value)
        {
            if (value.Contains(' ') || value.Contains(',') || value.Contains('='))
            {
                return "\"" + value.Replace("\"", "\\\"") + "\"";
            }
            return value;
        }
    }

    /// <summary>
    /// Metric type for classification.
    /// </summary>
    public enum DynatraceMetricType
    {
        /// <summary>
        /// Counter metric (monotonically increasing).
        /// </summary>
        Counter,

        /// <summary>
        /// Gauge metric (can increase or decrease).
        /// </summary>
        Gauge,

        /// <summary>
        /// Summary/histogram metric.
        /// </summary>
        Summary
    }

    #endregion

    #region Log Types

    /// <summary>
    /// Represents a Dynatrace log entry.
    /// </summary>
    public sealed class DynatraceLogEntry
    {
        /// <summary>
        /// Gets or sets the log message content.
        /// </summary>
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the timestamp in milliseconds since epoch.
        /// </summary>
        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the log level (INFO, WARN, ERROR, etc.).
        /// </summary>
        [JsonPropertyName("log.level")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LogLevel { get; set; }

        /// <summary>
        /// Gets or sets additional attributes.
        /// </summary>
        [JsonExtensionData]
        public Dictionary<string, object>? Attributes { get; set; }
    }

    /// <summary>
    /// Log level enumeration.
    /// </summary>
    public enum DynatraceLogLevel
    {
        /// <summary>
        /// Debug level.
        /// </summary>
        DEBUG,

        /// <summary>
        /// Info level.
        /// </summary>
        INFO,

        /// <summary>
        /// Warning level.
        /// </summary>
        WARN,

        /// <summary>
        /// Error level.
        /// </summary>
        ERROR,

        /// <summary>
        /// Fatal level.
        /// </summary>
        FATAL
    }

    #endregion

    #region Batch Types

    /// <summary>
    /// Batch of metrics for ingestion.
    /// </summary>
    internal sealed class MetricsBatch
    {
        public List<DynatraceMetric> Metrics { get; } = new();
        public DateTime CreatedAt { get; } = DateTime.UtcNow;

        public bool IsFull(int maxSize) => Metrics.Count >= maxSize;

        public bool ShouldFlush(TimeSpan maxAge) => DateTime.UtcNow - CreatedAt >= maxAge;

        public string ToLineProtocol()
        {
            var sb = new StringBuilder();
            foreach (var metric in Metrics)
            {
                sb.AppendLine(metric.ToLineProtocol());
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Batch of logs for ingestion.
    /// </summary>
    internal sealed class LogsBatch
    {
        public List<DynatraceLogEntry> Logs { get; } = new();
        public DateTime CreatedAt { get; } = DateTime.UtcNow;

        public bool IsFull(int maxSize) => Logs.Count >= maxSize;

        public bool ShouldFlush(TimeSpan maxAge) => DateTime.UtcNow - CreatedAt >= maxAge;
    }

    #endregion

    #region Helper Classes

    /// <summary>
    /// Dimension set for metrics.
    /// </summary>
    public sealed class DimensionSet
    {
        private readonly Dictionary<string, string> _dimensions;

        /// <summary>
        /// Gets an empty dimension set.
        /// </summary>
        public static DimensionSet Empty { get; } = new DimensionSet();

        /// <summary>
        /// Gets the dimensions as a read-only dictionary.
        /// </summary>
        public IReadOnlyDictionary<string, string> Dimensions => _dimensions;

        /// <summary>
        /// Initializes a new instance of the <see cref="DimensionSet"/> class.
        /// </summary>
        public DimensionSet()
        {
            _dimensions = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DimensionSet"/> class.
        /// </summary>
        /// <param name="dimensions">The dimensions to initialize with.</param>
        public DimensionSet(IEnumerable<KeyValuePair<string, string>> dimensions)
        {
            _dimensions = new Dictionary<string, string>(dimensions, StringComparer.Ordinal);
        }

        /// <summary>
        /// Creates a dimension set from key-value pairs.
        /// </summary>
        /// <param name="dimensions">The dimensions as tuples.</param>
        /// <returns>A new dimension set.</returns>
        public static DimensionSet From(params (string Key, string Value)[] dimensions)
        {
            return new DimensionSet(dimensions.Select(d => new KeyValuePair<string, string>(d.Key, d.Value)));
        }

        /// <summary>
        /// Creates a dimension set from a dictionary.
        /// </summary>
        /// <param name="dimensions">The dimensions dictionary.</param>
        /// <returns>A new dimension set.</returns>
        public static DimensionSet From(Dictionary<string, string> dimensions)
        {
            return new DimensionSet(dimensions);
        }

        /// <summary>
        /// Adds a dimension to the set.
        /// </summary>
        /// <param name="key">The dimension key.</param>
        /// <param name="value">The dimension value.</param>
        /// <returns>This dimension set for chaining.</returns>
        public DimensionSet Add(string key, string value)
        {
            _dimensions[key] = value;
            return this;
        }

        /// <summary>
        /// Converts the dimension set to a dictionary.
        /// </summary>
        /// <returns>A dictionary containing the dimensions.</returns>
        public Dictionary<string, string> ToDictionary()
        {
            return new Dictionary<string, string>(_dimensions, StringComparer.Ordinal);
        }
    }

    #endregion
}
