using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.GrafanaLoki
{
    #region Configuration

    /// <summary>
    /// Configuration options for the Grafana Loki plugin.
    /// </summary>
    public sealed class LokiConfiguration
    {
        /// <summary>
        /// Gets or sets the Loki Push API endpoint URL (e.g., http://localhost:3100).
        /// </summary>
        public string LokiUrl { get; set; } = "http://localhost:3100";

        /// <summary>
        /// Gets or sets the maximum number of log entries to batch before sending.
        /// </summary>
        public int BatchSize { get; set; } = 100;

        /// <summary>
        /// Gets or sets the flush interval in milliseconds for batched logs.
        /// </summary>
        public int FlushIntervalMs { get; set; } = 5000;

        /// <summary>
        /// Gets or sets the default labels to apply to all log streams.
        /// </summary>
        public Dictionary<string, string> DefaultLabels { get; set; } = new()
        {
            { "application", "datawarehouse" },
            { "environment", "production" }
        };

        /// <summary>
        /// Gets or sets the HTTP timeout in seconds.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets the maximum number of retry attempts for failed pushes.
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Gets or sets the retry delay in milliseconds.
        /// </summary>
        public int RetryDelayMs { get; set; } = 1000;

        /// <summary>
        /// Gets or sets whether to enable GZIP compression for payloads.
        /// </summary>
        public bool EnableCompression { get; set; } = true;

        /// <summary>
        /// Gets or sets the tenant ID for multi-tenancy (X-Scope-OrgID header).
        /// </summary>
        public string? TenantId { get; set; }

        /// <summary>
        /// Gets or sets the basic authentication username.
        /// </summary>
        public string? BasicAuthUsername { get; set; }

        /// <summary>
        /// Gets or sets the basic authentication password.
        /// </summary>
        public string? BasicAuthPassword { get; set; }

        /// <summary>
        /// Gets or sets custom HTTP headers to include in requests.
        /// </summary>
        public Dictionary<string, string>? CustomHeaders { get; set; }
    }

    #endregion

    #region Loki Push API Models

    /// <summary>
    /// Represents a Loki push request containing multiple streams.
    /// </summary>
    public sealed class LokiPushRequest
    {
        /// <summary>
        /// Gets or sets the list of log streams.
        /// </summary>
        [JsonPropertyName("streams")]
        public List<LokiStream> Streams { get; set; } = new();
    }

    /// <summary>
    /// Represents a single log stream with labels and values.
    /// </summary>
    public sealed class LokiStream
    {
        /// <summary>
        /// Gets or sets the stream labels as a dictionary.
        /// Example: { "job": "app", "level": "info" }
        /// </summary>
        [JsonPropertyName("stream")]
        public Dictionary<string, string> Stream { get; set; } = new();

        /// <summary>
        /// Gets or sets the log values as an array of [timestamp, line] tuples.
        /// Timestamp is nanoseconds since Unix epoch.
        /// </summary>
        [JsonPropertyName("values")]
        public List<List<string>> Values { get; set; } = new();
    }

    #endregion

    #region Internal Models

    /// <summary>
    /// Represents a single log entry before being batched into streams.
    /// </summary>
    internal sealed class LogEntry
    {
        /// <summary>
        /// Gets or sets the log message.
        /// </summary>
        public required string Message { get; set; }

        /// <summary>
        /// Gets or sets the timestamp of the log entry.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets the labels for this log entry.
        /// </summary>
        public Dictionary<string, string> Labels { get; set; } = new();

        /// <summary>
        /// Gets the stream key for grouping logs by labels.
        /// </summary>
        /// <returns>A string key representing the label combination.</returns>
        public string GetStreamKey()
        {
            var sortedLabels = Labels.OrderBy(kv => kv.Key);
            return string.Join("|", sortedLabels.Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }

    /// <summary>
    /// Statistics for the Loki plugin.
    /// </summary>
    public sealed class LokiStatistics
    {
        /// <summary>
        /// Gets or sets the total number of log entries sent.
        /// </summary>
        public long TotalLogsSent { get; set; }

        /// <summary>
        /// Gets or sets the total number of batches sent.
        /// </summary>
        public long TotalBatchesSent { get; set; }

        /// <summary>
        /// Gets or sets the total number of failed push attempts.
        /// </summary>
        public long TotalFailures { get; set; }

        /// <summary>
        /// Gets or sets the total number of retried push attempts.
        /// </summary>
        public long TotalRetries { get; set; }

        /// <summary>
        /// Gets or sets the number of logs currently in the buffer.
        /// </summary>
        public int BufferedLogs { get; set; }

        /// <summary>
        /// Gets or sets the average batch size.
        /// </summary>
        public double AverageBatchSize { get; set; }

        /// <summary>
        /// Gets or sets the last push timestamp.
        /// </summary>
        public DateTime? LastPushTime { get; set; }

        /// <summary>
        /// Gets or sets the last error message.
        /// </summary>
        public string? LastError { get; set; }

        /// <summary>
        /// Gets or sets the uptime in seconds.
        /// </summary>
        public double UptimeSeconds { get; set; }
    }

    #endregion

    #region Validation

    /// <summary>
    /// Label validation helper for Loki label names and values.
    /// </summary>
    public static class LokiLabelValidator
    {
        /// <summary>
        /// Validates a label name according to Loki conventions.
        /// Label names must match [a-zA-Z_][a-zA-Z0-9_]*
        /// </summary>
        /// <param name="name">The label name to validate.</param>
        /// <exception cref="ArgumentException">Thrown when the label name is invalid.</exception>
        public static void ValidateLabelName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Label name cannot be null or empty.", nameof(name));
            }

            if (!IsValidLabelName(name))
            {
                throw new ArgumentException(
                    $"Label name '{name}' is invalid. Must match [a-zA-Z_][a-zA-Z0-9_]*.",
                    nameof(name));
            }
        }

        /// <summary>
        /// Checks if a label name is valid according to Loki conventions.
        /// </summary>
        /// <param name="name">The label name to check.</param>
        /// <returns>True if valid; otherwise, false.</returns>
        public static bool IsValidLabelName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            var first = name[0];
            if (!char.IsLetter(first) && first != '_')
            {
                return false;
            }

            for (var i = 1; i < name.Length; i++)
            {
                var c = name[i];
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Sanitizes a label name by replacing invalid characters with underscores.
        /// </summary>
        /// <param name="name">The label name to sanitize.</param>
        /// <returns>A sanitized label name.</returns>
        public static string SanitizeLabelName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "_";
            }

            var chars = name.ToCharArray();

            // Fix first character
            if (!char.IsLetter(chars[0]) && chars[0] != '_')
            {
                chars[0] = '_';
            }

            // Fix remaining characters
            for (var i = 1; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_')
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        /// <summary>
        /// Validates label value (Loki accepts any string value).
        /// </summary>
        /// <param name="value">The label value.</param>
        /// <returns>The validated value (never null).</returns>
        public static string ValidateLabelValue(string? value)
        {
            return value ?? string.Empty;
        }
    }

    #endregion
}
