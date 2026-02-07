using System.Globalization;
using System.Text;

namespace DataWarehouse.Plugins.Chronograf
{
    #region Configuration

    /// <summary>
    /// Configuration options for the Chronograf/InfluxDB plugin.
    /// </summary>
    public sealed class ChronografConfiguration
    {
        /// <summary>
        /// Gets or sets the InfluxDB server URL.
        /// </summary>
        /// <remarks>WARNING: Default value is for development only. Configure for production.</remarks>
        public string InfluxDbUrl { get; set; } = "http://localhost:8086";

        /// <summary>
        /// Gets or sets the authentication token.
        /// </summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the organization name.
        /// </summary>
        public string Org { get; set; } = "default";

        /// <summary>
        /// Gets or sets the bucket name.
        /// </summary>
        public string Bucket { get; set; } = "default";

        /// <summary>
        /// Gets or sets the batch size for flushing metrics.
        /// </summary>
        public int BatchSize { get; set; } = 100;

        /// <summary>
        /// Gets or sets the flush interval in seconds.
        /// </summary>
        public int FlushIntervalSeconds { get; set; } = 10;

        /// <summary>
        /// Gets or sets the maximum retry attempts for failed writes.
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Gets or sets whether to include process metrics.
        /// </summary>
        public bool IncludeProcessMetrics { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to include runtime metrics.
        /// </summary>
        public bool IncludeRuntimeMetrics { get; set; } = true;

        /// <summary>
        /// Gets or sets the measurement prefix/namespace.
        /// </summary>
        public string? Namespace { get; set; }

        /// <summary>
        /// Gets or sets global tags to add to all metrics.
        /// </summary>
        public Dictionary<string, string>? GlobalTags { get; set; }

        /// <summary>
        /// Gets or sets the request timeout in seconds.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets whether to use gzip compression for requests.
        /// </summary>
        public bool EnableGzipCompression { get; set; } = true;
    }

    #endregion

    #region Line Protocol Types

    /// <summary>
    /// Represents an InfluxDB Line Protocol metric point.
    /// Format: measurement,tag1=value1,tag2=value2 field1=value1,field2=value2 timestamp
    /// </summary>
    public sealed class LineProtocolPoint
    {
        /// <summary>
        /// Gets or sets the measurement name.
        /// </summary>
        public string Measurement { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the tags (indexed metadata).
        /// </summary>
        public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets or sets the fields (actual metric values).
        /// </summary>
        public Dictionary<string, object> Fields { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets or sets the timestamp in nanoseconds since Unix epoch.
        /// </summary>
        public long? Timestamp { get; set; }

        /// <summary>
        /// Converts the point to InfluxDB Line Protocol format.
        /// </summary>
        /// <returns>The formatted line protocol string.</returns>
        public string ToLineProtocol()
        {
            var sb = new StringBuilder();

            // Measurement
            sb.Append(EscapeMeasurement(Measurement));

            // Tags
            if (Tags.Count > 0)
            {
                foreach (var tag in Tags.OrderBy(t => t.Key))
                {
                    sb.Append(',');
                    sb.Append(EscapeTag(tag.Key));
                    sb.Append('=');
                    sb.Append(EscapeTag(tag.Value));
                }
            }

            // Space separator
            sb.Append(' ');

            // Fields
            var firstField = true;
            foreach (var field in Fields)
            {
                if (!firstField)
                {
                    sb.Append(',');
                }
                firstField = false;

                sb.Append(EscapeField(field.Key));
                sb.Append('=');
                sb.Append(FormatFieldValue(field.Value));
            }

            // Timestamp (optional)
            if (Timestamp.HasValue)
            {
                sb.Append(' ');
                sb.Append(Timestamp.Value);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Escapes measurement names according to Line Protocol rules.
        /// </summary>
        private static string EscapeMeasurement(string value)
        {
            return value
                .Replace(",", "\\,")
                .Replace(" ", "\\ ");
        }

        /// <summary>
        /// Escapes tag keys and values according to Line Protocol rules.
        /// </summary>
        private static string EscapeTag(string value)
        {
            return value
                .Replace(",", "\\,")
                .Replace("=", "\\=")
                .Replace(" ", "\\ ");
        }

        /// <summary>
        /// Escapes field keys according to Line Protocol rules.
        /// </summary>
        private static string EscapeField(string value)
        {
            return value
                .Replace(",", "\\,")
                .Replace("=", "\\=")
                .Replace(" ", "\\ ");
        }

        /// <summary>
        /// Formats field values according to Line Protocol type rules.
        /// </summary>
        private static string FormatFieldValue(object value)
        {
            return value switch
            {
                // Integers must end with 'i'
                int i => i.ToString(CultureInfo.InvariantCulture) + "i",
                long l => l.ToString(CultureInfo.InvariantCulture) + "i",
                short s => s.ToString(CultureInfo.InvariantCulture) + "i",
                byte b => b.ToString(CultureInfo.InvariantCulture) + "i",

                // Floats are default
                double d => FormatDouble(d),
                float f => FormatDouble(f),
                decimal dec => FormatDouble((double)dec),

                // Booleans
                bool b => b ? "true" : "false",

                // Strings must be quoted and escaped
                string str => "\"" + str.Replace("\"", "\\\"") + "\"",

                // Default to string representation
                _ => "\"" + value.ToString()?.Replace("\"", "\\\"") + "\""
            };
        }

        /// <summary>
        /// Formats double values for Line Protocol.
        /// </summary>
        private static string FormatDouble(double value)
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
            return value.ToString("G17", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Builder for creating Line Protocol points.
    /// </summary>
    public sealed class LineProtocolPointBuilder
    {
        private readonly LineProtocolPoint _point = new();

        /// <summary>
        /// Sets the measurement name.
        /// </summary>
        public LineProtocolPointBuilder Measurement(string measurement)
        {
            _point.Measurement = measurement;
            return this;
        }

        /// <summary>
        /// Adds a tag.
        /// </summary>
        public LineProtocolPointBuilder Tag(string key, string value)
        {
            _point.Tags[key] = value;
            return this;
        }

        /// <summary>
        /// Adds multiple tags.
        /// </summary>
        public LineProtocolPointBuilder Tags(Dictionary<string, string> tags)
        {
            foreach (var tag in tags)
            {
                _point.Tags[tag.Key] = tag.Value;
            }
            return this;
        }

        /// <summary>
        /// Adds a field.
        /// </summary>
        public LineProtocolPointBuilder Field(string key, object value)
        {
            _point.Fields[key] = value;
            return this;
        }

        /// <summary>
        /// Adds multiple fields.
        /// </summary>
        public LineProtocolPointBuilder Fields(Dictionary<string, object> fields)
        {
            foreach (var field in fields)
            {
                _point.Fields[field.Key] = field.Value;
            }
            return this;
        }

        /// <summary>
        /// Sets the timestamp.
        /// </summary>
        public LineProtocolPointBuilder Timestamp(long timestamp)
        {
            _point.Timestamp = timestamp;
            return this;
        }

        /// <summary>
        /// Sets the timestamp from a DateTime.
        /// </summary>
        public LineProtocolPointBuilder Timestamp(DateTime dateTime)
        {
            _point.Timestamp = new DateTimeOffset(dateTime.ToUniversalTime()).ToUnixTimeMilliseconds() * 1_000_000;
            return this;
        }

        /// <summary>
        /// Builds the point.
        /// </summary>
        public LineProtocolPoint Build()
        {
            if (string.IsNullOrWhiteSpace(_point.Measurement))
            {
                throw new InvalidOperationException("Measurement is required.");
            }
            if (_point.Fields.Count == 0)
            {
                throw new InvalidOperationException("At least one field is required.");
            }
            return _point;
        }
    }

    #endregion

    #region Batch Writer

    /// <summary>
    /// Manages batching and flushing of metrics to InfluxDB.
    /// </summary>
    internal sealed class LineProtocolBatch
    {
        private readonly List<LineProtocolPoint> _points = new();
        private readonly object _lock = new();
        private readonly int _maxBatchSize;

        /// <summary>
        /// Gets the current batch size.
        /// </summary>
        public int Count
        {
            get { lock (_lock) { return _points.Count; } }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LineProtocolBatch"/> class.
        /// </summary>
        /// <param name="maxBatchSize">The maximum batch size before auto-flush.</param>
        public LineProtocolBatch(int maxBatchSize = 100)
        {
            _maxBatchSize = maxBatchSize;
        }

        /// <summary>
        /// Adds a point to the batch.
        /// </summary>
        /// <param name="point">The point to add.</param>
        /// <returns>True if the batch should be flushed; otherwise, false.</returns>
        public bool Add(LineProtocolPoint point)
        {
            lock (_lock)
            {
                _points.Add(point);
                return _points.Count >= _maxBatchSize;
            }
        }

        /// <summary>
        /// Gets all points and clears the batch.
        /// </summary>
        /// <returns>The batch of points.</returns>
        public List<LineProtocolPoint> GetAndClear()
        {
            lock (_lock)
            {
                var result = new List<LineProtocolPoint>(_points);
                _points.Clear();
                return result;
            }
        }

        /// <summary>
        /// Converts the batch to Line Protocol format.
        /// </summary>
        /// <returns>The formatted batch as a single string.</returns>
        public string ToLineProtocol()
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                foreach (var point in _points)
                {
                    sb.AppendLine(point.ToLineProtocol());
                }
                return sb.ToString();
            }
        }
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Tracks statistics for the InfluxDB plugin.
    /// </summary>
    public sealed class InfluxDbStatistics
    {
        private long _pointsWritten;
        private long _batchesFlushed;
        private long _writeErrors;
        private long _retries;

        /// <summary>
        /// Gets the total number of points written.
        /// </summary>
        public long PointsWritten => Interlocked.Read(ref _pointsWritten);

        /// <summary>
        /// Gets the total number of batches flushed.
        /// </summary>
        public long BatchesFlushed => Interlocked.Read(ref _batchesFlushed);

        /// <summary>
        /// Gets the total number of write errors.
        /// </summary>
        public long WriteErrors => Interlocked.Read(ref _writeErrors);

        /// <summary>
        /// Gets the total number of retries.
        /// </summary>
        public long Retries => Interlocked.Read(ref _retries);

        /// <summary>
        /// Increments the points written counter.
        /// </summary>
        public void IncrementPointsWritten(int count = 1)
        {
            Interlocked.Add(ref _pointsWritten, count);
        }

        /// <summary>
        /// Increments the batches flushed counter.
        /// </summary>
        public void IncrementBatchesFlushed()
        {
            Interlocked.Increment(ref _batchesFlushed);
        }

        /// <summary>
        /// Increments the write errors counter.
        /// </summary>
        public void IncrementWriteErrors()
        {
            Interlocked.Increment(ref _writeErrors);
        }

        /// <summary>
        /// Increments the retries counter.
        /// </summary>
        public void IncrementRetries()
        {
            Interlocked.Increment(ref _retries);
        }
    }

    #endregion
}
