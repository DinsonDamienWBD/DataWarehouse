using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace DataWarehouse.Plugins.Prometheus
{
    #region Enums

    /// <summary>
    /// Defines the types of metrics supported by Prometheus.
    /// </summary>
    public enum MetricType
    {
        /// <summary>
        /// A counter is a cumulative metric that only increases (or resets to zero on restart).
        /// </summary>
        Counter,

        /// <summary>
        /// A gauge is a metric that can go up or down.
        /// </summary>
        Gauge,

        /// <summary>
        /// A histogram samples observations and counts them in configurable buckets.
        /// </summary>
        Histogram,

        /// <summary>
        /// A summary samples observations and provides configurable quantiles over a sliding time window.
        /// </summary>
        Summary
    }

    #endregion

    #region Label Types

    /// <summary>
    /// Represents a set of labels (key-value pairs) for a metric.
    /// Labels provide dimensions for metrics and must be consistent within a metric family.
    /// </summary>
    public sealed class LabelSet : IEquatable<LabelSet>
    {
        private readonly SortedDictionary<string, string> _labels;
        private readonly int _hashCode;

        /// <summary>
        /// Gets the labels as a read-only dictionary.
        /// </summary>
        public IReadOnlyDictionary<string, string> Labels => _labels;

        /// <summary>
        /// Gets an empty label set.
        /// </summary>
        public static LabelSet Empty { get; } = new LabelSet(new Dictionary<string, string>());

        /// <summary>
        /// Initializes a new instance of the <see cref="LabelSet"/> class.
        /// </summary>
        /// <param name="labels">The labels to include in the set.</param>
        /// <exception cref="ArgumentNullException">Thrown when labels is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a label name or value is invalid.</exception>
        public LabelSet(IEnumerable<KeyValuePair<string, string>> labels)
        {
            ArgumentNullException.ThrowIfNull(labels);

            _labels = new SortedDictionary<string, string>(StringComparer.Ordinal);

            foreach (var label in labels)
            {
                ValidateLabelName(label.Key);
                _labels[label.Key] = label.Value ?? string.Empty;
            }

            _hashCode = ComputeHashCode();
        }

        /// <summary>
        /// Creates a label set from the specified key-value pairs.
        /// </summary>
        /// <param name="labels">The labels as tuples.</param>
        /// <returns>A new <see cref="LabelSet"/>.</returns>
        public static LabelSet From(params (string Name, string Value)[] labels)
        {
            return new LabelSet(labels.Select(l => new KeyValuePair<string, string>(l.Name, l.Value)));
        }

        /// <summary>
        /// Creates a label set from a dictionary.
        /// </summary>
        /// <param name="labels">The labels dictionary.</param>
        /// <returns>A new <see cref="LabelSet"/>.</returns>
        public static LabelSet From(IDictionary<string, string> labels)
        {
            return new LabelSet(labels);
        }

        /// <summary>
        /// Validates that a label name conforms to Prometheus naming conventions.
        /// </summary>
        /// <param name="name">The label name to validate.</param>
        /// <exception cref="ArgumentException">Thrown when the name is invalid.</exception>
        public static void ValidateLabelName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Label name cannot be null or empty.", nameof(name));
            }

            // Labels starting with __ are reserved for internal use
            if (name.StartsWith("__", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Label name '{name}' is reserved (starts with '__').", nameof(name));
            }

            // Must match [a-zA-Z_][a-zA-Z0-9_]*
            if (!IsValidPrometheusName(name))
            {
                throw new ArgumentException(
                    $"Label name '{name}' is invalid. Must match [a-zA-Z_][a-zA-Z0-9_]*.",
                    nameof(name));
            }
        }

        /// <summary>
        /// Checks if a name is valid according to Prometheus naming rules.
        /// </summary>
        private static bool IsValidPrometheusName(string name)
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
        /// Formats the labels for Prometheus text exposition format.
        /// </summary>
        /// <returns>The formatted label string, or empty string if no labels.</returns>
        public string ToPrometheusFormat()
        {
            if (_labels.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder("{");
            var first = true;

            foreach (var label in _labels)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                sb.Append(label.Key);
                sb.Append("=\"");
                sb.Append(EscapeLabelValue(label.Value));
                sb.Append('"');
            }

            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Escapes a label value for Prometheus format.
        /// </summary>
        private static string EscapeLabelValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n");
        }

        private int ComputeHashCode()
        {
            var hash = new HashCode();
            foreach (var label in _labels)
            {
                hash.Add(label.Key);
                hash.Add(label.Value);
            }
            return hash.ToHashCode();
        }

        /// <inheritdoc/>
        public bool Equals(LabelSet? other)
        {
            if (other is null)
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (_labels.Count != other._labels.Count)
            {
                return false;
            }

            foreach (var label in _labels)
            {
                if (!other._labels.TryGetValue(label.Key, out var otherValue) ||
                    label.Value != otherValue)
                {
                    return false;
                }
            }

            return true;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj) => Equals(obj as LabelSet);

        /// <inheritdoc/>
        public override int GetHashCode() => _hashCode;

        /// <inheritdoc/>
        public override string ToString() => ToPrometheusFormat();
    }

    #endregion

    #region Metric Family

    /// <summary>
    /// Represents a family of metrics with the same name but different label values.
    /// </summary>
    public sealed class MetricFamily
    {
        private readonly ConcurrentDictionary<LabelSet, MetricValue> _metrics;
        private readonly object _lock = new();

        /// <summary>
        /// Gets the name of the metric family.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the type of metrics in this family.
        /// </summary>
        public MetricType Type { get; }

        /// <summary>
        /// Gets the help text (description) for this metric family.
        /// </summary>
        public string Help { get; }

        /// <summary>
        /// Gets the label names used by this metric family.
        /// </summary>
        public IReadOnlyList<string> LabelNames { get; }

        /// <summary>
        /// Gets the histogram bucket boundaries (only for histogram type).
        /// </summary>
        public IReadOnlyList<double>? Buckets { get; }

        /// <summary>
        /// Gets the summary quantiles (only for summary type).
        /// </summary>
        public IReadOnlyList<QuantileDefinition>? Quantiles { get; }

        /// <summary>
        /// Gets the number of metric instances in this family.
        /// </summary>
        public int Count => _metrics.Count;

        /// <summary>
        /// Gets all metric values in this family.
        /// </summary>
        public IEnumerable<KeyValuePair<LabelSet, MetricValue>> Metrics => _metrics;

        /// <summary>
        /// Initializes a new instance of the <see cref="MetricFamily"/> class.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <param name="type">The metric type.</param>
        /// <param name="help">The help text.</param>
        /// <param name="labelNames">The label names for this family.</param>
        /// <param name="buckets">The histogram buckets (optional).</param>
        /// <param name="quantiles">The summary quantiles (optional).</param>
        public MetricFamily(
            string name,
            MetricType type,
            string help,
            IEnumerable<string>? labelNames = null,
            IEnumerable<double>? buckets = null,
            IEnumerable<QuantileDefinition>? quantiles = null)
        {
            ValidateMetricName(name);

            Name = name;
            Type = type;
            Help = help ?? string.Empty;
            LabelNames = labelNames?.ToList().AsReadOnly() ?? Array.Empty<string>().AsReadOnly();
            Buckets = buckets?.OrderBy(b => b).ToList().AsReadOnly();
            Quantiles = quantiles?.ToList().AsReadOnly();
            _metrics = new ConcurrentDictionary<LabelSet, MetricValue>();

            // Validate label names
            foreach (var labelName in LabelNames)
            {
                LabelSet.ValidateLabelName(labelName);
            }
        }

        /// <summary>
        /// Validates a metric name according to Prometheus conventions.
        /// </summary>
        /// <param name="name">The metric name to validate.</param>
        /// <exception cref="ArgumentException">Thrown when the name is invalid.</exception>
        public static void ValidateMetricName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Metric name cannot be null or empty.", nameof(name));
            }

            // Must match [a-zA-Z_:][a-zA-Z0-9_:]*
            var first = name[0];
            if (!char.IsLetter(first) && first != '_' && first != ':')
            {
                throw new ArgumentException(
                    $"Metric name '{name}' is invalid. Must start with [a-zA-Z_:].",
                    nameof(name));
            }

            for (var i = 1; i < name.Length; i++)
            {
                var c = name[i];
                if (!char.IsLetterOrDigit(c) && c != '_' && c != ':')
                {
                    throw new ArgumentException(
                        $"Metric name '{name}' contains invalid character '{c}'. Must match [a-zA-Z0-9_:].",
                        nameof(name));
                }
            }
        }

        /// <summary>
        /// Gets or creates a metric value for the specified labels.
        /// </summary>
        /// <param name="labels">The label values.</param>
        /// <returns>The metric value.</returns>
        public MetricValue GetOrCreate(LabelSet labels)
        {
            return _metrics.GetOrAdd(labels, _ => CreateMetricValue());
        }

        /// <summary>
        /// Removes a metric instance with the specified labels.
        /// </summary>
        /// <param name="labels">The label values to remove.</param>
        /// <returns>True if removed; otherwise, false.</returns>
        public bool Remove(LabelSet labels)
        {
            return _metrics.TryRemove(labels, out _);
        }

        /// <summary>
        /// Clears all metric instances in this family.
        /// </summary>
        public void Clear()
        {
            _metrics.Clear();
        }

        private MetricValue CreateMetricValue()
        {
            return Type switch
            {
                MetricType.Counter => new CounterValue(),
                MetricType.Gauge => new GaugeValue(),
                MetricType.Histogram => new HistogramValue(Buckets ?? DefaultBuckets.Default),
                MetricType.Summary => new SummaryValue(Quantiles ?? DefaultQuantiles.Default),
                _ => throw new InvalidOperationException($"Unknown metric type: {Type}")
            };
        }

        /// <summary>
        /// Formats the metric family for Prometheus text exposition.
        /// </summary>
        /// <returns>The formatted metric text.</returns>
        public string ToPrometheusFormat()
        {
            var sb = new StringBuilder();

            // HELP line
            if (!string.IsNullOrEmpty(Help))
            {
                sb.Append("# HELP ");
                sb.Append(Name);
                sb.Append(' ');
                sb.AppendLine(EscapeHelpText(Help));
            }

            // TYPE line
            sb.Append("# TYPE ");
            sb.Append(Name);
            sb.Append(' ');
            sb.AppendLine(Type.ToString().ToLowerInvariant());

            // Metric values
            foreach (var kvp in _metrics)
            {
                var labelStr = kvp.Key.ToPrometheusFormat();
                kvp.Value.AppendToPrometheus(sb, Name, labelStr);
            }

            return sb.ToString();
        }

        private static string EscapeHelpText(string text)
        {
            return text
                .Replace("\\", "\\\\")
                .Replace("\n", "\\n");
        }
    }

    #endregion

    #region Metric Values

    /// <summary>
    /// Base class for metric values.
    /// </summary>
    public abstract class MetricValue
    {
        /// <summary>
        /// Gets the timestamp of the last update.
        /// </summary>
        public DateTime LastUpdated { get; protected set; } = DateTime.UtcNow;

        /// <summary>
        /// Appends the metric value to a StringBuilder in Prometheus format.
        /// </summary>
        /// <param name="sb">The StringBuilder to append to.</param>
        /// <param name="name">The metric name.</param>
        /// <param name="labels">The formatted labels string.</param>
        public abstract void AppendToPrometheus(StringBuilder sb, string name, string labels);

        /// <summary>
        /// Formats a double value for Prometheus output.
        /// </summary>
        protected static string FormatValue(double value)
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
    /// Represents a counter metric value (monotonically increasing).
    /// </summary>
    public sealed class CounterValue : MetricValue
    {
        private double _value;
        private readonly object _lock = new();

        /// <summary>
        /// Gets the current counter value.
        /// </summary>
        public double Value
        {
            get { lock (_lock) { return _value; } }
        }

        /// <summary>
        /// Increments the counter by 1.
        /// </summary>
        public void Inc()
        {
            Inc(1);
        }

        /// <summary>
        /// Increments the counter by the specified amount.
        /// </summary>
        /// <param name="amount">The amount to increment (must be non-negative).</param>
        /// <exception cref="ArgumentException">Thrown when amount is negative.</exception>
        public void Inc(double amount)
        {
            if (amount < 0)
            {
                throw new ArgumentException("Counter increment amount must be non-negative.", nameof(amount));
            }

            lock (_lock)
            {
                _value += amount;
                LastUpdated = DateTime.UtcNow;
            }
        }

        /// <inheritdoc/>
        public override void AppendToPrometheus(StringBuilder sb, string name, string labels)
        {
            sb.Append(name);
            sb.Append(labels);
            sb.Append(' ');
            sb.AppendLine(FormatValue(Value));
        }
    }

    /// <summary>
    /// Represents a gauge metric value (can increase or decrease).
    /// </summary>
    public sealed class GaugeValue : MetricValue
    {
        private double _value;
        private readonly object _lock = new();

        /// <summary>
        /// Gets the current gauge value.
        /// </summary>
        public double Value
        {
            get { lock (_lock) { return _value; } }
        }

        /// <summary>
        /// Sets the gauge to the specified value.
        /// </summary>
        /// <param name="value">The value to set.</param>
        public void Set(double value)
        {
            lock (_lock)
            {
                _value = value;
                LastUpdated = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Increments the gauge by 1.
        /// </summary>
        public void Inc()
        {
            Inc(1);
        }

        /// <summary>
        /// Increments the gauge by the specified amount.
        /// </summary>
        /// <param name="amount">The amount to increment.</param>
        public void Inc(double amount)
        {
            lock (_lock)
            {
                _value += amount;
                LastUpdated = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Decrements the gauge by 1.
        /// </summary>
        public void Dec()
        {
            Dec(1);
        }

        /// <summary>
        /// Decrements the gauge by the specified amount.
        /// </summary>
        /// <param name="amount">The amount to decrement.</param>
        public void Dec(double amount)
        {
            lock (_lock)
            {
                _value -= amount;
                LastUpdated = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Sets the gauge to the current Unix timestamp in seconds.
        /// </summary>
        public void SetToCurrentTime()
        {
            Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        /// <inheritdoc/>
        public override void AppendToPrometheus(StringBuilder sb, string name, string labels)
        {
            sb.Append(name);
            sb.Append(labels);
            sb.Append(' ');
            sb.AppendLine(FormatValue(Value));
        }
    }

    /// <summary>
    /// Represents a histogram metric value with configurable buckets.
    /// </summary>
    public sealed class HistogramValue : MetricValue
    {
        private readonly double[] _bucketBounds;
        private readonly long[] _bucketCounts;
        private long _count;
        private double _sum;
        private readonly object _lock = new();

        /// <summary>
        /// Gets the bucket boundaries.
        /// </summary>
        public IReadOnlyList<double> Buckets => _bucketBounds;

        /// <summary>
        /// Gets the total count of observations.
        /// </summary>
        public long Count
        {
            get { lock (_lock) { return _count; } }
        }

        /// <summary>
        /// Gets the sum of all observed values.
        /// </summary>
        public double Sum
        {
            get { lock (_lock) { return _sum; } }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="HistogramValue"/> class.
        /// </summary>
        /// <param name="buckets">The bucket boundaries.</param>
        public HistogramValue(IEnumerable<double> buckets)
        {
            _bucketBounds = buckets.OrderBy(b => b).ToArray();
            _bucketCounts = new long[_bucketBounds.Length + 1]; // +1 for +Inf bucket
        }

        /// <summary>
        /// Observes a value, incrementing the appropriate bucket and the sum.
        /// </summary>
        /// <param name="value">The observed value.</param>
        public void Observe(double value)
        {
            lock (_lock)
            {
                // Find the bucket for this value
                var bucketIndex = Array.BinarySearch(_bucketBounds, value);
                if (bucketIndex < 0)
                {
                    bucketIndex = ~bucketIndex;
                }

                // Increment all buckets >= value (cumulative)
                for (var i = bucketIndex; i < _bucketCounts.Length; i++)
                {
                    _bucketCounts[i]++;
                }

                _count++;
                _sum += value;
                LastUpdated = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Gets the bucket counts.
        /// </summary>
        /// <returns>An array of cumulative bucket counts.</returns>
        public long[] GetBucketCounts()
        {
            lock (_lock)
            {
                return (long[])_bucketCounts.Clone();
            }
        }

        /// <inheritdoc/>
        public override void AppendToPrometheus(StringBuilder sb, string name, string labels)
        {
            lock (_lock)
            {
                var baseLabels = labels;
                var hasLabels = !string.IsNullOrEmpty(baseLabels);

                // Output each bucket
                for (var i = 0; i < _bucketBounds.Length; i++)
                {
                    sb.Append(name);
                    sb.Append("_bucket");

                    if (hasLabels)
                    {
                        // Insert le label into existing labels
                        sb.Append(baseLabels.TrimEnd('}'));
                        sb.Append(",le=\"");
                    }
                    else
                    {
                        sb.Append("{le=\"");
                    }

                    sb.Append(FormatValue(_bucketBounds[i]));
                    sb.Append("\"} ");
                    sb.AppendLine(_bucketCounts[i].ToString(CultureInfo.InvariantCulture));
                }

                // +Inf bucket
                sb.Append(name);
                sb.Append("_bucket");
                if (hasLabels)
                {
                    sb.Append(baseLabels.TrimEnd('}'));
                    sb.Append(",le=\"+Inf\"} ");
                }
                else
                {
                    sb.Append("{le=\"+Inf\"} ");
                }
                sb.AppendLine(_bucketCounts[^1].ToString(CultureInfo.InvariantCulture));

                // Sum
                sb.Append(name);
                sb.Append("_sum");
                sb.Append(labels);
                sb.Append(' ');
                sb.AppendLine(FormatValue(_sum));

                // Count
                sb.Append(name);
                sb.Append("_count");
                sb.Append(labels);
                sb.Append(' ');
                sb.AppendLine(_count.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>
    /// Represents a summary metric value with configurable quantiles.
    /// </summary>
    public sealed class SummaryValue : MetricValue
    {
        private readonly QuantileDefinition[] _quantiles;
        private readonly SlidingWindowQuantileEstimator _estimator;
        private long _count;
        private double _sum;
        private readonly object _lock = new();

        /// <summary>
        /// Gets the quantile definitions.
        /// </summary>
        public IReadOnlyList<QuantileDefinition> Quantiles => _quantiles;

        /// <summary>
        /// Gets the total count of observations.
        /// </summary>
        public long Count
        {
            get { lock (_lock) { return _count; } }
        }

        /// <summary>
        /// Gets the sum of all observed values.
        /// </summary>
        public double Sum
        {
            get { lock (_lock) { return _sum; } }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SummaryValue"/> class.
        /// </summary>
        /// <param name="quantiles">The quantile definitions.</param>
        /// <param name="maxAge">The maximum age for observations in the sliding window.</param>
        /// <param name="ageBuckets">The number of age buckets for the sliding window.</param>
        public SummaryValue(
            IEnumerable<QuantileDefinition> quantiles,
            TimeSpan? maxAge = null,
            int ageBuckets = 5)
        {
            _quantiles = quantiles.OrderBy(q => q.Quantile).ToArray();
            _estimator = new SlidingWindowQuantileEstimator(
                maxAge ?? TimeSpan.FromMinutes(10),
                ageBuckets);
        }

        /// <summary>
        /// Observes a value.
        /// </summary>
        /// <param name="value">The observed value.</param>
        public void Observe(double value)
        {
            lock (_lock)
            {
                _estimator.Observe(value);
                _count++;
                _sum += value;
                LastUpdated = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Gets the current quantile values.
        /// </summary>
        /// <returns>A dictionary of quantile to value.</returns>
        public Dictionary<double, double> GetQuantileValues()
        {
            lock (_lock)
            {
                var result = new Dictionary<double, double>();
                foreach (var q in _quantiles)
                {
                    result[q.Quantile] = _estimator.Query(q.Quantile);
                }
                return result;
            }
        }

        /// <inheritdoc/>
        public override void AppendToPrometheus(StringBuilder sb, string name, string labels)
        {
            lock (_lock)
            {
                var baseLabels = labels;
                var hasLabels = !string.IsNullOrEmpty(baseLabels);

                // Output each quantile
                foreach (var q in _quantiles)
                {
                    var value = _estimator.Query(q.Quantile);

                    sb.Append(name);
                    if (hasLabels)
                    {
                        sb.Append(baseLabels.TrimEnd('}'));
                        sb.Append(",quantile=\"");
                    }
                    else
                    {
                        sb.Append("{quantile=\"");
                    }

                    sb.Append(FormatValue(q.Quantile));
                    sb.Append("\"} ");
                    sb.AppendLine(FormatValue(value));
                }

                // Sum
                sb.Append(name);
                sb.Append("_sum");
                sb.Append(labels);
                sb.Append(' ');
                sb.AppendLine(FormatValue(_sum));

                // Count
                sb.Append(name);
                sb.Append("_count");
                sb.Append(labels);
                sb.Append(' ');
                sb.AppendLine(_count.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    #endregion

    #region Quantile Support

    /// <summary>
    /// Defines a quantile with its allowed error margin.
    /// </summary>
    public sealed class QuantileDefinition
    {
        /// <summary>
        /// Gets the quantile value (between 0 and 1).
        /// </summary>
        public double Quantile { get; }

        /// <summary>
        /// Gets the allowed error margin.
        /// </summary>
        public double Error { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="QuantileDefinition"/> class.
        /// </summary>
        /// <param name="quantile">The quantile value (0 to 1).</param>
        /// <param name="error">The allowed error margin.</param>
        public QuantileDefinition(double quantile, double error)
        {
            if (quantile < 0 || quantile > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(quantile), "Quantile must be between 0 and 1.");
            }

            if (error < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(error), "Error must be non-negative.");
            }

            Quantile = quantile;
            Error = error;
        }
    }

    /// <summary>
    /// Sliding window quantile estimator using age-bucketed samples.
    /// </summary>
    internal sealed class SlidingWindowQuantileEstimator
    {
        private readonly TimeSpan _maxAge;
        private readonly int _ageBuckets;
        private readonly List<TimestampedValue>[] _buckets;
        private int _currentBucket;
        private DateTime _lastRotation;
        private readonly object _lock = new();

        private readonly struct TimestampedValue
        {
            public double Value { get; }
            public DateTime Timestamp { get; }

            public TimestampedValue(double value, DateTime timestamp)
            {
                Value = value;
                Timestamp = timestamp;
            }
        }

        public SlidingWindowQuantileEstimator(TimeSpan maxAge, int ageBuckets)
        {
            _maxAge = maxAge;
            _ageBuckets = ageBuckets;
            _buckets = new List<TimestampedValue>[ageBuckets];
            for (var i = 0; i < ageBuckets; i++)
            {
                _buckets[i] = new List<TimestampedValue>();
            }
            _currentBucket = 0;
            _lastRotation = DateTime.UtcNow;
        }

        public void Observe(double value)
        {
            lock (_lock)
            {
                RotateIfNeeded();
                _buckets[_currentBucket].Add(new TimestampedValue(value, DateTime.UtcNow));
            }
        }

        public double Query(double quantile)
        {
            lock (_lock)
            {
                RotateIfNeeded();

                var allValues = new List<double>();
                var cutoff = DateTime.UtcNow - _maxAge;

                foreach (var bucket in _buckets)
                {
                    foreach (var tv in bucket)
                    {
                        if (tv.Timestamp >= cutoff)
                        {
                            allValues.Add(tv.Value);
                        }
                    }
                }

                if (allValues.Count == 0)
                {
                    return double.NaN;
                }

                allValues.Sort();
                var index = (int)Math.Ceiling(quantile * allValues.Count) - 1;
                index = Math.Max(0, Math.Min(index, allValues.Count - 1));
                return allValues[index];
            }
        }

        private void RotateIfNeeded()
        {
            var bucketDuration = TimeSpan.FromTicks(_maxAge.Ticks / _ageBuckets);
            var now = DateTime.UtcNow;

            while (now - _lastRotation >= bucketDuration)
            {
                _currentBucket = (_currentBucket + 1) % _ageBuckets;
                _buckets[_currentBucket].Clear();
                _lastRotation += bucketDuration;
            }
        }
    }

    #endregion

    #region Default Buckets and Quantiles

    /// <summary>
    /// Provides default histogram bucket configurations.
    /// </summary>
    public static class DefaultBuckets
    {
        /// <summary>
        /// Default buckets for general-purpose histograms.
        /// </summary>
        public static readonly IReadOnlyList<double> Default = new[]
        {
            0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1.0, 2.5, 5.0, 7.5, 10.0
        };

        /// <summary>
        /// Linear buckets from start to start + width * count.
        /// </summary>
        /// <param name="start">The starting value.</param>
        /// <param name="width">The width of each bucket.</param>
        /// <param name="count">The number of buckets.</param>
        /// <returns>An array of bucket boundaries.</returns>
        public static double[] Linear(double start, double width, int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Count must be positive.");
            }

            var buckets = new double[count];
            for (var i = 0; i < count; i++)
            {
                buckets[i] = start + i * width;
            }
            return buckets;
        }

        /// <summary>
        /// Exponential buckets from start * factor^0 to start * factor^(count-1).
        /// </summary>
        /// <param name="start">The starting value.</param>
        /// <param name="factor">The exponential factor.</param>
        /// <param name="count">The number of buckets.</param>
        /// <returns>An array of bucket boundaries.</returns>
        public static double[] Exponential(double start, double factor, int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Count must be positive.");
            }

            if (start <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(start), "Start must be positive.");
            }

            if (factor <= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(factor), "Factor must be greater than 1.");
            }

            var buckets = new double[count];
            for (var i = 0; i < count; i++)
            {
                buckets[i] = start * Math.Pow(factor, i);
            }
            return buckets;
        }
    }

    /// <summary>
    /// Provides default quantile configurations for summaries.
    /// </summary>
    public static class DefaultQuantiles
    {
        /// <summary>
        /// Default quantiles for general-purpose summaries.
        /// </summary>
        public static readonly IReadOnlyList<QuantileDefinition> Default = new[]
        {
            new QuantileDefinition(0.5, 0.05),
            new QuantileDefinition(0.9, 0.01),
            new QuantileDefinition(0.99, 0.001)
        };
    }

    #endregion

    #region Metric Registry

    /// <summary>
    /// Registry for managing metric families with label cardinality controls.
    /// </summary>
    public sealed class MetricRegistry
    {
        private readonly ConcurrentDictionary<string, MetricFamily> _families;
        private readonly int _maxLabelCardinality;
        private readonly int _maxMetricFamilies;
        private readonly object _lock = new();

        /// <summary>
        /// Gets the number of registered metric families.
        /// </summary>
        public int FamilyCount => _families.Count;

        /// <summary>
        /// Gets all registered metric families.
        /// </summary>
        public IEnumerable<MetricFamily> Families => _families.Values;

        /// <summary>
        /// Event raised when a metric family is created.
        /// </summary>
        public event Action<MetricFamily>? OnFamilyCreated;

        /// <summary>
        /// Event raised when label cardinality limit is reached.
        /// </summary>
        public event Action<string, int>? OnCardinalityLimitReached;

        /// <summary>
        /// Initializes a new instance of the <see cref="MetricRegistry"/> class.
        /// </summary>
        /// <param name="maxLabelCardinality">Maximum number of label combinations per metric family.</param>
        /// <param name="maxMetricFamilies">Maximum number of metric families.</param>
        public MetricRegistry(int maxLabelCardinality = 10000, int maxMetricFamilies = 1000)
        {
            _maxLabelCardinality = maxLabelCardinality;
            _maxMetricFamilies = maxMetricFamilies;
            _families = new ConcurrentDictionary<string, MetricFamily>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Creates or gets a counter metric family.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <param name="help">The help text.</param>
        /// <param name="labelNames">The label names.</param>
        /// <returns>The metric family.</returns>
        public MetricFamily CreateCounter(string name, string help, params string[] labelNames)
        {
            return GetOrCreateFamily(name, MetricType.Counter, help, labelNames);
        }

        /// <summary>
        /// Creates or gets a gauge metric family.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <param name="help">The help text.</param>
        /// <param name="labelNames">The label names.</param>
        /// <returns>The metric family.</returns>
        public MetricFamily CreateGauge(string name, string help, params string[] labelNames)
        {
            return GetOrCreateFamily(name, MetricType.Gauge, help, labelNames);
        }

        /// <summary>
        /// Creates or gets a histogram metric family.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <param name="help">The help text.</param>
        /// <param name="buckets">The bucket boundaries.</param>
        /// <param name="labelNames">The label names.</param>
        /// <returns>The metric family.</returns>
        public MetricFamily CreateHistogram(
            string name,
            string help,
            IEnumerable<double>? buckets = null,
            params string[] labelNames)
        {
            return GetOrCreateFamily(name, MetricType.Histogram, help, labelNames, buckets);
        }

        /// <summary>
        /// Creates or gets a summary metric family.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <param name="help">The help text.</param>
        /// <param name="quantiles">The quantile definitions.</param>
        /// <param name="labelNames">The label names.</param>
        /// <returns>The metric family.</returns>
        public MetricFamily CreateSummary(
            string name,
            string help,
            IEnumerable<QuantileDefinition>? quantiles = null,
            params string[] labelNames)
        {
            return GetOrCreateFamily(name, MetricType.Summary, help, labelNames, quantiles: quantiles);
        }

        /// <summary>
        /// Gets a metric family by name.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <returns>The metric family, or null if not found.</returns>
        public MetricFamily? GetFamily(string name)
        {
            return _families.TryGetValue(name, out var family) ? family : null;
        }

        /// <summary>
        /// Removes a metric family by name.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <returns>True if removed; otherwise, false.</returns>
        public bool RemoveFamily(string name)
        {
            return _families.TryRemove(name, out _);
        }

        /// <summary>
        /// Clears all metric families.
        /// </summary>
        public void Clear()
        {
            _families.Clear();
        }

        /// <summary>
        /// Exports all metrics in Prometheus text format.
        /// </summary>
        /// <returns>The metrics in Prometheus text exposition format.</returns>
        public string ExportToPrometheusFormat()
        {
            var sb = new StringBuilder();

            foreach (var family in _families.Values)
            {
                sb.Append(family.ToPrometheusFormat());
            }

            return sb.ToString();
        }

        private MetricFamily GetOrCreateFamily(
            string name,
            MetricType type,
            string help,
            IEnumerable<string>? labelNames = null,
            IEnumerable<double>? buckets = null,
            IEnumerable<QuantileDefinition>? quantiles = null)
        {
            return _families.GetOrAdd(name, _ =>
            {
                if (_families.Count >= _maxMetricFamilies)
                {
                    throw new InvalidOperationException(
                        $"Maximum number of metric families ({_maxMetricFamilies}) exceeded.");
                }

                var family = new MetricFamily(name, type, help, labelNames, buckets, quantiles);
                OnFamilyCreated?.Invoke(family);
                return family;
            });
        }

        /// <summary>
        /// Checks if adding a label set to a family would exceed cardinality limits.
        /// </summary>
        /// <param name="family">The metric family.</param>
        /// <returns>True if the cardinality is within limits; otherwise, false.</returns>
        public bool CheckCardinality(MetricFamily family)
        {
            if (family.Count >= _maxLabelCardinality)
            {
                OnCardinalityLimitReached?.Invoke(family.Name, family.Count);
                return false;
            }
            return true;
        }
    }

    #endregion

    #region Prometheus Configuration

    /// <summary>
    /// Configuration options for the Prometheus plugin.
    /// </summary>
    public sealed class PrometheusConfiguration
    {
        /// <summary>
        /// Gets or sets the port for the metrics HTTP endpoint.
        /// </summary>
        public int Port { get; set; } = 9090;

        /// <summary>
        /// Gets or sets the path for the metrics endpoint.
        /// </summary>
        public string MetricsPath { get; set; } = "/metrics";

        /// <summary>
        /// Gets or sets whether to include process metrics.
        /// </summary>
        public bool IncludeProcessMetrics { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to include runtime metrics.
        /// </summary>
        public bool IncludeRuntimeMetrics { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum number of label combinations per metric.
        /// </summary>
        public int MaxLabelCardinality { get; set; } = 10000;

        /// <summary>
        /// Gets or sets the maximum number of metric families.
        /// </summary>
        public int MaxMetricFamilies { get; set; } = 1000;

        /// <summary>
        /// Gets or sets the default histogram buckets.
        /// </summary>
        public IReadOnlyList<double>? DefaultHistogramBuckets { get; set; }

        /// <summary>
        /// Gets or sets the default summary quantiles.
        /// </summary>
        public IReadOnlyList<QuantileDefinition>? DefaultSummaryQuantiles { get; set; }

        /// <summary>
        /// Gets or sets the namespace prefix for metrics.
        /// </summary>
        public string? Namespace { get; set; }

        /// <summary>
        /// Gets or sets constant labels to add to all metrics.
        /// </summary>
        public Dictionary<string, string>? ConstLabels { get; set; }

        /// <summary>
        /// Gets or sets whether to enable GZIP compression for responses.
        /// </summary>
        public bool EnableGzipCompression { get; set; } = true;

        /// <summary>
        /// Gets or sets the host address to bind to.
        /// </summary>
        public string Host { get; set; } = "*";
    }

    #endregion
}
