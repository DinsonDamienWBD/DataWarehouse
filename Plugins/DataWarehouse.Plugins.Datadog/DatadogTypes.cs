using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.Datadog
{
    #region Configuration

    /// <summary>
    /// Configuration options for the Datadog plugin.
    /// </summary>
    public sealed class DatadogConfiguration
    {
        /// <summary>
        /// Gets or sets the Datadog API key for authentication.
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Datadog site (e.g., "datadoghq.com", "datadoghq.eu", "us3.datadoghq.com", "us5.datadoghq.com").
        /// </summary>
        public string Site { get; set; } = "datadoghq.com";

        /// <summary>
        /// Gets or sets the service name for tagging.
        /// </summary>
        public string ServiceName { get; set; } = "datawarehouse";

        /// <summary>
        /// Gets or sets the environment name (e.g., "production", "staging", "development").
        /// </summary>
        public string Environment { get; set; } = "production";

        /// <summary>
        /// Gets or sets the service version.
        /// </summary>
        public string? Version { get; set; }

        /// <summary>
        /// Gets or sets global tags to add to all metrics and logs.
        /// </summary>
        public Dictionary<string, string> Tags { get; set; } = new();

        /// <summary>
        /// Gets or sets the hostname for the metrics source.
        /// </summary>
        public string? Hostname { get; set; }

        /// <summary>
        /// Gets or sets the batch size for metrics submissions.
        /// </summary>
        public int MetricsBatchSize { get; set; } = 100;

        /// <summary>
        /// Gets or sets the batch size for logs submissions.
        /// </summary>
        public int LogsBatchSize { get; set; } = 100;

        /// <summary>
        /// Gets or sets the flush interval for batched data.
        /// </summary>
        public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets or sets the HTTP request timeout.
        /// </summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets whether to enable metric aggregation before sending.
        /// </summary>
        public bool EnableMetricAggregation { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable automatic error logging.
        /// </summary>
        public bool EnableErrorLogging { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum queue size for pending metrics and logs.
        /// </summary>
        public int MaxQueueSize { get; set; } = 10000;

        /// <summary>
        /// Gets the metrics API endpoint.
        /// </summary>
        [JsonIgnore]
        public string MetricsEndpoint => $"https://api.{Site}/api/v2/series";

        /// <summary>
        /// Gets the logs API endpoint.
        /// </summary>
        [JsonIgnore]
        public string LogsEndpoint => $"https://http-intake.logs.{Site}/api/v2/logs";

        /// <summary>
        /// Validates the configuration.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when configuration is invalid.</exception>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                throw new InvalidOperationException("Datadog API key is required.");
            }

            if (string.IsNullOrWhiteSpace(Site))
            {
                throw new InvalidOperationException("Datadog site is required.");
            }

            if (string.IsNullOrWhiteSpace(ServiceName))
            {
                throw new InvalidOperationException("Service name is required.");
            }

            if (MetricsBatchSize <= 0)
            {
                throw new InvalidOperationException("Metrics batch size must be positive.");
            }

            if (LogsBatchSize <= 0)
            {
                throw new InvalidOperationException("Logs batch size must be positive.");
            }

            if (MaxQueueSize <= 0)
            {
                throw new InvalidOperationException("Max queue size must be positive.");
            }
        }
    }

    #endregion

    #region Metric Types

    /// <summary>
    /// Represents the type of Datadog metric.
    /// </summary>
    public enum DatadogMetricType
    {
        /// <summary>
        /// A gauge metric that can go up or down.
        /// </summary>
        Gauge,

        /// <summary>
        /// A counter metric that monotonically increases.
        /// </summary>
        Count,

        /// <summary>
        /// A rate metric (count per second).
        /// </summary>
        Rate
    }

    /// <summary>
    /// Represents a Datadog metric point.
    /// </summary>
    public sealed class DatadogMetricPoint
    {
        /// <summary>
        /// Gets or sets the metric name.
        /// </summary>
        [JsonPropertyName("metric")]
        public string Metric { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the metric type.
        /// </summary>
        [JsonPropertyName("type")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DatadogMetricType Type { get; set; }

        /// <summary>
        /// Gets or sets the data points (timestamp, value pairs).
        /// </summary>
        [JsonPropertyName("points")]
        public List<DatadogPoint> Points { get; set; } = new();

        /// <summary>
        /// Gets or sets the tags for this metric.
        /// </summary>
        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new();

        /// <summary>
        /// Gets or sets the hostname.
        /// </summary>
        [JsonPropertyName("host")]
        public string? Host { get; set; }

        /// <summary>
        /// Gets or sets the interval for rate metrics.
        /// </summary>
        [JsonPropertyName("interval")]
        public int? Interval { get; set; }
    }

    /// <summary>
    /// Represents a timestamp-value pair for Datadog metrics.
    /// </summary>
    public sealed class DatadogPoint
    {
        /// <summary>
        /// Gets or sets the timestamp (Unix epoch seconds).
        /// </summary>
        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the metric value.
        /// </summary>
        [JsonPropertyName("value")]
        public double Value { get; set; }
    }

    /// <summary>
    /// Represents a Datadog metrics submission request.
    /// </summary>
    public sealed class DatadogMetricsRequest
    {
        /// <summary>
        /// Gets or sets the series of metrics to submit.
        /// </summary>
        [JsonPropertyName("series")]
        public List<DatadogMetricPoint> Series { get; set; } = new();
    }

    #endregion

    #region Log Types

    /// <summary>
    /// Represents a Datadog log entry.
    /// </summary>
    public sealed class DatadogLogEntry
    {
        /// <summary>
        /// Gets or sets the log message.
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the log level (e.g., "info", "warn", "error").
        /// </summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = "info";

        /// <summary>
        /// Gets or sets the service name.
        /// </summary>
        [JsonPropertyName("service")]
        public string Service { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the hostname.
        /// </summary>
        [JsonPropertyName("hostname")]
        public string? Hostname { get; set; }

        /// <summary>
        /// Gets or sets the timestamp (ISO 8601 format).
        /// </summary>
        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the tags for this log entry.
        /// </summary>
        [JsonPropertyName("ddtags")]
        public string? Tags { get; set; }

        /// <summary>
        /// Gets or sets additional attributes.
        /// </summary>
        [JsonPropertyName("attributes")]
        public Dictionary<string, object>? Attributes { get; set; }
    }

    #endregion

    #region Internal Types

    /// <summary>
    /// Internal metric accumulator for aggregation.
    /// </summary>
    internal sealed class MetricAccumulator
    {
        private readonly object _lock = new();
        private double _value;
        private long _count;
        private double _sum;
        private double _min = double.MaxValue;
        private double _max = double.MinValue;
        private DateTime _lastUpdate;

        public string Name { get; }
        public DatadogMetricType Type { get; }
        public List<string> Tags { get; }

        public MetricAccumulator(string name, DatadogMetricType type, List<string> tags)
        {
            Name = name;
            Type = type;
            Tags = tags;
            _lastUpdate = DateTime.UtcNow;
        }

        public void Record(double value)
        {
            lock (_lock)
            {
                switch (Type)
                {
                    case DatadogMetricType.Gauge:
                        _value = value;
                        break;
                    case DatadogMetricType.Count:
                    case DatadogMetricType.Rate:
                        _value += value;
                        break;
                }

                _count++;
                _sum += value;
                _min = Math.Min(_min, value);
                _max = Math.Max(_max, value);
                _lastUpdate = DateTime.UtcNow;
            }
        }

        public (double Value, long Count, DateTime LastUpdate) Flush()
        {
            lock (_lock)
            {
                var result = (_value, _count, _lastUpdate);

                // Reset for count/rate types
                if (Type == DatadogMetricType.Count || Type == DatadogMetricType.Rate)
                {
                    _value = 0;
                }

                _count = 0;
                _sum = 0;
                _min = double.MaxValue;
                _max = double.MinValue;

                return result;
            }
        }

        public string GetKey()
        {
            var tagString = string.Join(",", Tags.OrderBy(t => t));
            return $"{Name}:{Type}:{tagString}";
        }
    }

    #endregion

    #region Tag Helper

    /// <summary>
    /// Helper class for working with Datadog tags.
    /// </summary>
    public static class DatadogTagHelper
    {
        /// <summary>
        /// Formats tags as Datadog tag strings (key:value).
        /// </summary>
        /// <param name="tags">The tags dictionary.</param>
        /// <returns>A list of formatted tag strings.</returns>
        public static List<string> FormatTags(Dictionary<string, string>? tags)
        {
            if (tags == null || tags.Count == 0)
            {
                return new List<string>();
            }

            return tags
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
                .Select(kv => $"{SanitizeTagKey(kv.Key)}:{SanitizeTagValue(kv.Value ?? string.Empty)}")
                .ToList();
        }

        /// <summary>
        /// Merges multiple tag dictionaries with later ones taking precedence.
        /// </summary>
        /// <param name="tagSets">The tag dictionaries to merge.</param>
        /// <returns>A merged tag list.</returns>
        public static List<string> MergeTags(params Dictionary<string, string>?[] tagSets)
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var tagSet in tagSets)
            {
                if (tagSet != null)
                {
                    foreach (var kv in tagSet)
                    {
                        merged[kv.Key] = kv.Value;
                    }
                }
            }

            return FormatTags(merged);
        }

        /// <summary>
        /// Parses a tag string (key:value or key=value) into a key-value pair.
        /// </summary>
        /// <param name="tag">The tag string.</param>
        /// <returns>A key-value pair, or null if invalid.</returns>
        public static (string Key, string Value)? ParseTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            var colonIndex = tag.IndexOf(':');
            if (colonIndex > 0)
            {
                return (tag.Substring(0, colonIndex), tag.Substring(colonIndex + 1));
            }

            var equalsIndex = tag.IndexOf('=');
            if (equalsIndex > 0)
            {
                return (tag.Substring(0, equalsIndex), tag.Substring(equalsIndex + 1));
            }

            return null;
        }

        private static string SanitizeTagKey(string key)
        {
            // Datadog tag keys must start with a letter and contain only alphanumerics, underscores, minuses, colons, periods, and slashes
            if (string.IsNullOrWhiteSpace(key))
            {
                return "unknown";
            }

            // Replace invalid characters with underscores
            var sanitized = new string(key.Select(c =>
                char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == ':' || c == '.' || c == '/'
                    ? c
                    : '_').ToArray());

            // Ensure it starts with a letter
            if (!char.IsLetter(sanitized[0]))
            {
                sanitized = "t_" + sanitized;
            }

            return sanitized.ToLowerInvariant();
        }

        private static string SanitizeTagValue(string value)
        {
            // Tag values are more permissive, but we'll remove control characters
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return new string(value.Where(c => !char.IsControl(c)).ToArray());
        }
    }

    #endregion
}
