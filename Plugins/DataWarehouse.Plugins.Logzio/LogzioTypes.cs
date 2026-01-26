using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.Logzio
{
    #region Configuration

    /// <summary>
    /// Configuration options for the Logz.io plugin.
    /// </summary>
    public sealed class LogzioConfiguration
    {
        /// <summary>
        /// Gets or sets the Logz.io listener URL for logs.
        /// </summary>
        public string ListenerUrl { get; set; } = "https://listener.logz.io:8071";

        /// <summary>
        /// Gets or sets the Logz.io token for authentication.
        /// </summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the log type identifier.
        /// </summary>
        public string Type { get; set; } = "datawarehouse";

        /// <summary>
        /// Gets or sets the metrics listener URL (Prometheus remote write endpoint).
        /// </summary>
        public string MetricsListenerUrl { get; set; } = "https://listener.logz.io:8053";

        /// <summary>
        /// Gets or sets whether to enable log shipping.
        /// </summary>
        public bool EnableLogs { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable metrics shipping.
        /// </summary>
        public bool EnableMetrics { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum number of logs to buffer before sending.
        /// </summary>
        public int MaxBatchSize { get; set; } = 100;

        /// <summary>
        /// Gets or sets the maximum time to buffer logs before sending.
        /// </summary>
        public TimeSpan MaxBatchInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets or sets the maximum number of retries for failed requests.
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Gets or sets the timeout for HTTP requests.
        /// </summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets whether to enable GZIP compression.
        /// </summary>
        public bool EnableCompression { get; set; } = true;

        /// <summary>
        /// Gets or sets additional fields to include with all logs.
        /// </summary>
        public Dictionary<string, object>? AdditionalFields { get; set; }

        /// <summary>
        /// Gets or sets the interval for metrics shipping.
        /// </summary>
        public TimeSpan MetricsInterval { get; set; } = TimeSpan.FromSeconds(15);
    }

    #endregion

    #region Log Entry

    /// <summary>
    /// Represents a log entry for Logz.io.
    /// </summary>
    public sealed class LogzioLogEntry
    {
        /// <summary>
        /// Gets or sets the log message.
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the log level.
        /// </summary>
        [JsonPropertyName("level")]
        public string Level { get; set; } = "INFO";

        /// <summary>
        /// Gets or sets the timestamp in ISO 8601 format.
        /// </summary>
        [JsonPropertyName("@timestamp")]
        public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");

        /// <summary>
        /// Gets or sets the log type.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets additional fields.
        /// </summary>
        [JsonExtensionData]
        public Dictionary<string, object>? AdditionalFields { get; set; }
    }

    #endregion

    #region Metrics Types

    /// <summary>
    /// Represents a metric sample for Logz.io (Prometheus format).
    /// </summary>
    public sealed class LogzioMetricSample
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
        /// Gets or sets the timestamp in milliseconds since Unix epoch.
        /// </summary>
        public long Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the metric labels.
        /// </summary>
        public Dictionary<string, string> Labels { get; set; } = new();
    }

    /// <summary>
    /// Represents a Prometheus remote write request for Logz.io metrics.
    /// </summary>
    public sealed class PrometheusRemoteWriteRequest
    {
        /// <summary>
        /// Gets or sets the time series data.
        /// </summary>
        public List<TimeSeries> Timeseries { get; set; } = new();
    }

    /// <summary>
    /// Represents a time series in Prometheus remote write format.
    /// </summary>
    public sealed class TimeSeries
    {
        /// <summary>
        /// Gets or sets the labels.
        /// </summary>
        public List<Label> Labels { get; set; } = new();

        /// <summary>
        /// Gets or sets the samples.
        /// </summary>
        public List<Sample> Samples { get; set; } = new();
    }

    /// <summary>
    /// Represents a label in Prometheus format.
    /// </summary>
    public sealed class Label
    {
        /// <summary>
        /// Gets or sets the label name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the label value.
        /// </summary>
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents a sample in Prometheus format.
    /// </summary>
    public sealed class Sample
    {
        /// <summary>
        /// Gets or sets the sample value.
        /// </summary>
        public double Value { get; set; }

        /// <summary>
        /// Gets or sets the timestamp in milliseconds.
        /// </summary>
        public long Timestamp { get; set; }
    }

    #endregion

    #region Enums

    /// <summary>
    /// Log severity levels matching standard logging levels.
    /// </summary>
    public enum LogLevel
    {
        /// <summary>
        /// Trace level logging.
        /// </summary>
        Trace,

        /// <summary>
        /// Debug level logging.
        /// </summary>
        Debug,

        /// <summary>
        /// Information level logging.
        /// </summary>
        Info,

        /// <summary>
        /// Warning level logging.
        /// </summary>
        Warn,

        /// <summary>
        /// Error level logging.
        /// </summary>
        Error,

        /// <summary>
        /// Fatal level logging.
        /// </summary>
        Fatal
    }

    #endregion
}
