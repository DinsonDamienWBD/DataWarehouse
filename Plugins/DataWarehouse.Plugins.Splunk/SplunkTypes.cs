using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.Splunk
{
    /// <summary>
    /// Represents a Splunk HEC event payload.
    /// </summary>
    public sealed class SplunkEvent
    {
        /// <summary>
        /// Gets or sets the event timestamp (Unix epoch time in seconds).
        /// </summary>
        [JsonPropertyName("time")]
        public double? Time { get; set; }

        /// <summary>
        /// Gets or sets the host field.
        /// </summary>
        [JsonPropertyName("host")]
        public string? Host { get; set; }

        /// <summary>
        /// Gets or sets the source field.
        /// </summary>
        [JsonPropertyName("source")]
        public string? Source { get; set; }

        /// <summary>
        /// Gets or sets the source type field.
        /// </summary>
        [JsonPropertyName("sourcetype")]
        public string? SourceType { get; set; }

        /// <summary>
        /// Gets or sets the index field.
        /// </summary>
        [JsonPropertyName("index")]
        public string? Index { get; set; }

        /// <summary>
        /// Gets or sets the event data (can be string or structured object).
        /// </summary>
        [JsonPropertyName("event")]
        public object? Event { get; set; }

        /// <summary>
        /// Gets or sets custom fields to add to the event.
        /// </summary>
        [JsonPropertyName("fields")]
        public Dictionary<string, object>? Fields { get; set; }
    }

    /// <summary>
    /// Represents a Splunk HEC metric payload.
    /// </summary>
    public sealed class SplunkMetric
    {
        /// <summary>
        /// Gets or sets the metric timestamp (Unix epoch time in seconds).
        /// </summary>
        [JsonPropertyName("time")]
        public double? Time { get; set; }

        /// <summary>
        /// Gets or sets the host field.
        /// </summary>
        [JsonPropertyName("host")]
        public string? Host { get; set; }

        /// <summary>
        /// Gets or sets the source field.
        /// </summary>
        [JsonPropertyName("source")]
        public string? Source { get; set; }

        /// <summary>
        /// Gets or sets the source type field.
        /// </summary>
        [JsonPropertyName("sourcetype")]
        public string? SourceType { get; set; }

        /// <summary>
        /// Gets or sets the index field.
        /// </summary>
        [JsonPropertyName("index")]
        public string? Index { get; set; }

        /// <summary>
        /// Gets or sets the event data containing metric information.
        /// </summary>
        [JsonPropertyName("event")]
        public string Event { get; set; } = "metric";

        /// <summary>
        /// Gets or sets the metric fields (must include 'metric_name' and numeric metric values).
        /// </summary>
        [JsonPropertyName("fields")]
        public Dictionary<string, object>? Fields { get; set; }
    }

    /// <summary>
    /// Represents the response from a Splunk HEC submission.
    /// </summary>
    public sealed class SplunkResponse
    {
        /// <summary>
        /// Gets or sets the response text.
        /// </summary>
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        /// <summary>
        /// Gets or sets the response code.
        /// </summary>
        [JsonPropertyName("code")]
        public int? Code { get; set; }

        /// <summary>
        /// Gets or sets the invalid event number (if any).
        /// </summary>
        [JsonPropertyName("invalid-event-number")]
        public int? InvalidEventNumber { get; set; }

        /// <summary>
        /// Gets whether the submission was successful.
        /// </summary>
        [JsonIgnore]
        public bool IsSuccess => Code == 0 || Text?.Equals("Success", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Represents statistics about event submissions.
    /// </summary>
    public sealed class SubmissionStatistics
    {
        private long _totalEvents;
        private long _totalMetrics;
        private long _successfulSubmissions;
        private long _failedSubmissions;
        private long _totalRetries;
        private long _bytesSent;

        /// <summary>
        /// Gets the total number of events submitted.
        /// </summary>
        public long TotalEvents => Interlocked.Read(ref _totalEvents);

        /// <summary>
        /// Gets the total number of metrics submitted.
        /// </summary>
        public long TotalMetrics => Interlocked.Read(ref _totalMetrics);

        /// <summary>
        /// Gets the total number of successful submissions.
        /// </summary>
        public long SuccessfulSubmissions => Interlocked.Read(ref _successfulSubmissions);

        /// <summary>
        /// Gets the total number of failed submissions.
        /// </summary>
        public long FailedSubmissions => Interlocked.Read(ref _failedSubmissions);

        /// <summary>
        /// Gets the total number of retries.
        /// </summary>
        public long TotalRetries => Interlocked.Read(ref _totalRetries);

        /// <summary>
        /// Gets the total bytes sent.
        /// </summary>
        public long BytesSent => Interlocked.Read(ref _bytesSent);

        /// <summary>
        /// Gets or sets the last submission timestamp.
        /// </summary>
        public DateTime? LastSubmission { get; set; }

        /// <summary>
        /// Gets or sets the last error message.
        /// </summary>
        public string? LastError { get; set; }

        /// <summary>
        /// Gets or sets the last error timestamp.
        /// </summary>
        public DateTime? LastErrorTime { get; set; }

        /// <summary>
        /// Adds to the total events count.
        /// </summary>
        internal void AddEvents(long count) => Interlocked.Add(ref _totalEvents, count);

        /// <summary>
        /// Adds to the total metrics count.
        /// </summary>
        internal void AddMetrics(long count) => Interlocked.Add(ref _totalMetrics, count);

        /// <summary>
        /// Increments successful submissions.
        /// </summary>
        internal void IncrementSuccessful() => Interlocked.Increment(ref _successfulSubmissions);

        /// <summary>
        /// Increments failed submissions.
        /// </summary>
        internal void IncrementFailed() => Interlocked.Increment(ref _failedSubmissions);

        /// <summary>
        /// Increments retries.
        /// </summary>
        internal void IncrementRetries() => Interlocked.Increment(ref _totalRetries);

        /// <summary>
        /// Adds to bytes sent.
        /// </summary>
        internal void AddBytesSent(long bytes) => Interlocked.Add(ref _bytesSent, bytes);

        /// <summary>
        /// Clears all statistics.
        /// </summary>
        internal void Clear()
        {
            Interlocked.Exchange(ref _totalEvents, 0);
            Interlocked.Exchange(ref _totalMetrics, 0);
            Interlocked.Exchange(ref _successfulSubmissions, 0);
            Interlocked.Exchange(ref _failedSubmissions, 0);
            Interlocked.Exchange(ref _totalRetries, 0);
            Interlocked.Exchange(ref _bytesSent, 0);
            LastSubmission = null;
            LastError = null;
            LastErrorTime = null;
        }
    }

    /// <summary>
    /// Represents a batch of events waiting to be submitted.
    /// </summary>
    internal sealed class EventBatch
    {
        /// <summary>
        /// Gets the list of events in this batch.
        /// </summary>
        public List<SplunkEvent> Events { get; } = new();

        /// <summary>
        /// Gets the list of metrics in this batch.
        /// </summary>
        public List<SplunkMetric> Metrics { get; } = new();

        /// <summary>
        /// Gets the timestamp when this batch was created.
        /// </summary>
        public DateTime CreatedAt { get; } = DateTime.UtcNow;

        /// <summary>
        /// Gets whether this batch is empty.
        /// </summary>
        public bool IsEmpty => Events.Count == 0 && Metrics.Count == 0;

        /// <summary>
        /// Gets the total count of items in this batch.
        /// </summary>
        public int Count => Events.Count + Metrics.Count;
    }
}
