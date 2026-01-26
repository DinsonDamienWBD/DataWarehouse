using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.NewRelic
{
    #region Configuration

    /// <summary>
    /// Configuration options for the New Relic plugin.
    /// </summary>
    public sealed class NewRelicConfiguration
    {
        /// <summary>
        /// Gets or sets the New Relic API Key (required for authentication).
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the New Relic Account ID.
        /// </summary>
        public string AccountId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the New Relic region (US or EU).
        /// </summary>
        public NewRelicRegion Region { get; set; } = NewRelicRegion.US;

        /// <summary>
        /// Gets or sets whether to enable metric reporting.
        /// </summary>
        public bool EnableMetrics { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable log reporting.
        /// </summary>
        public bool EnableLogs { get; set; } = true;

        /// <summary>
        /// Gets or sets the batch size for metrics.
        /// </summary>
        public int MetricBatchSize { get; set; } = 100;

        /// <summary>
        /// Gets or sets the batch size for logs.
        /// </summary>
        public int LogBatchSize { get; set; } = 100;

        /// <summary>
        /// Gets or sets the flush interval in seconds.
        /// </summary>
        public int FlushIntervalSeconds { get; set; } = 10;

        /// <summary>
        /// Gets or sets the default service name for metrics and logs.
        /// </summary>
        public string ServiceName { get; set; } = "DataWarehouse";

        /// <summary>
        /// Gets or sets common attributes to add to all metrics and logs.
        /// </summary>
        public Dictionary<string, object>? CommonAttributes { get; set; }

        /// <summary>
        /// Gets or sets the HTTP timeout in seconds.
        /// </summary>
        public int HttpTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets the maximum retry attempts for failed requests.
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Gets the Metric API endpoint based on the region.
        /// </summary>
        public string MetricApiEndpoint => Region == NewRelicRegion.US
            ? "https://metric-api.newrelic.com/metric/v1"
            : "https://metric-api.eu.newrelic.com/metric/v1";

        /// <summary>
        /// Gets the Log API endpoint based on the region.
        /// </summary>
        public string LogApiEndpoint => Region == NewRelicRegion.US
            ? "https://log-api.newrelic.com/log/v1"
            : "https://log-api.eu.newrelic.com/log/v1";
    }

    /// <summary>
    /// New Relic region enumeration.
    /// </summary>
    public enum NewRelicRegion
    {
        /// <summary>
        /// United States region.
        /// </summary>
        US,

        /// <summary>
        /// European Union region.
        /// </summary>
        EU
    }

    #endregion

    #region Metric Types

    /// <summary>
    /// Represents a New Relic metric type.
    /// </summary>
    public enum NewRelicMetricType
    {
        /// <summary>
        /// A counter metric that only increases.
        /// </summary>
        Count,

        /// <summary>
        /// A gauge metric that can increase or decrease.
        /// </summary>
        Gauge,

        /// <summary>
        /// A summary metric with count, sum, min, and max.
        /// </summary>
        Summary
    }

    /// <summary>
    /// Represents a New Relic metric data point.
    /// </summary>
    public sealed class NewRelicMetric
    {
        /// <summary>
        /// Gets or sets the metric name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the metric type.
        /// </summary>
        [JsonPropertyName("type")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public NewRelicMetricType Type { get; set; }

        /// <summary>
        /// Gets or sets the metric value (for count and gauge types).
        /// </summary>
        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Value { get; set; }

        /// <summary>
        /// Gets or sets the timestamp in Unix epoch milliseconds.
        /// </summary>
        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the metric attributes (dimensions).
        /// </summary>
        [JsonPropertyName("attributes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object>? Attributes { get; set; }

        /// <summary>
        /// Gets or sets the interval in milliseconds (for summary type).
        /// </summary>
        [JsonPropertyName("interval.ms")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? IntervalMs { get; set; }

        /// <summary>
        /// Gets or sets the count (for summary type).
        /// </summary>
        [JsonPropertyName("count")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Count { get; set; }

        /// <summary>
        /// Gets or sets the sum (for summary type).
        /// </summary>
        [JsonPropertyName("sum")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Sum { get; set; }

        /// <summary>
        /// Gets or sets the minimum value (for summary type).
        /// </summary>
        [JsonPropertyName("min")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Min { get; set; }

        /// <summary>
        /// Gets or sets the maximum value (for summary type).
        /// </summary>
        [JsonPropertyName("max")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Max { get; set; }
    }

    /// <summary>
    /// Represents a batch of metrics to send to New Relic.
    /// </summary>
    internal sealed class MetricBatch
    {
        [JsonPropertyName("metrics")]
        public List<NewRelicMetric> Metrics { get; set; } = new();

        [JsonPropertyName("common")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CommonAttributes? Common { get; set; }
    }

    /// <summary>
    /// Common attributes for all metrics in a batch.
    /// </summary>
    internal sealed class CommonAttributes
    {
        [JsonPropertyName("timestamp")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Timestamp { get; set; }

        [JsonPropertyName("interval.ms")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? IntervalMs { get; set; }

        [JsonPropertyName("attributes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object>? Attributes { get; set; }
    }

    /// <summary>
    /// Wrapper for one or more metric batches.
    /// </summary>
    internal sealed class MetricPayload
    {
        [JsonPropertyName("metrics")]
        public List<MetricBatch> Metrics { get; set; } = new();
    }

    #endregion

    #region Log Types

    /// <summary>
    /// Represents a New Relic log entry.
    /// </summary>
    public sealed class NewRelicLogEntry
    {
        /// <summary>
        /// Gets or sets the log message.
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the timestamp in Unix epoch milliseconds.
        /// </summary>
        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the log level.
        /// </summary>
        [JsonPropertyName("level")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Level { get; set; }

        /// <summary>
        /// Gets or sets custom attributes for the log entry.
        /// </summary>
        [JsonPropertyName("attributes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object>? Attributes { get; set; }
    }

    /// <summary>
    /// Wrapper for log entries payload.
    /// </summary>
    internal sealed class LogPayload
    {
        [JsonPropertyName("logs")]
        public List<NewRelicLogEntry> Logs { get; set; } = new();

        [JsonPropertyName("common")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LogCommonAttributes? Common { get; set; }
    }

    /// <summary>
    /// Common attributes for all logs in a batch.
    /// </summary>
    internal sealed class LogCommonAttributes
    {
        [JsonPropertyName("attributes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object>? Attributes { get; set; }
    }

    #endregion

    #region Summary Aggregator

    /// <summary>
    /// Aggregates metric values for summary type metrics.
    /// </summary>
    internal sealed class SummaryAggregator
    {
        private long _count;
        private double _sum;
        private double _min = double.MaxValue;
        private double _max = double.MinValue;
        private readonly object _lock = new();

        public void Observe(double value)
        {
            lock (_lock)
            {
                _count++;
                _sum += value;
                if (value < _min) _min = value;
                if (value > _max) _max = value;
            }
        }

        public (long count, double sum, double min, double max) GetAndReset()
        {
            lock (_lock)
            {
                var result = (_count, _sum, _min, _max);
                _count = 0;
                _sum = 0;
                _min = double.MaxValue;
                _max = double.MinValue;
                return result;
            }
        }

        public bool HasData()
        {
            lock (_lock)
            {
                return _count > 0;
            }
        }
    }

    #endregion
}
