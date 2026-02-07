using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.Kibana
{
    #region Configuration

    /// <summary>
    /// Configuration options for the Kibana/Elasticsearch plugin.
    /// </summary>
    public sealed class KibanaConfiguration
    {
        /// <summary>
        /// Gets or sets the Elasticsearch URL (e.g., "http://localhost:9200").
        /// </summary>
        /// <remarks>WARNING: Default value is for development only. Configure for production.</remarks>
        public string ElasticsearchUrl { get; set; } = "http://localhost:9200";

        /// <summary>
        /// Gets or sets the index prefix for all indices (e.g., "datawarehouse").
        /// </summary>
        public string IndexPrefix { get; set; } = "datawarehouse";

        /// <summary>
        /// Gets or sets the API key for authentication (optional).
        /// </summary>
        public string? ApiKey { get; set; }

        /// <summary>
        /// Gets or sets the username for basic authentication (optional).
        /// </summary>
        public string? Username { get; set; }

        /// <summary>
        /// Gets or sets the password for basic authentication (optional).
        /// </summary>
        public string? Password { get; set; }

        /// <summary>
        /// Gets or sets the batch size for bulk operations.
        /// </summary>
        public int BulkBatchSize { get; set; } = 500;

        /// <summary>
        /// Gets or sets the flush interval for buffered documents.
        /// </summary>
        public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets whether to create index templates automatically.
        /// </summary>
        public bool AutoCreateIndexTemplates { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable GZIP compression for requests.
        /// </summary>
        public bool EnableCompression { get; set; } = true;

        /// <summary>
        /// Gets or sets the request timeout.
        /// </summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the number of shards for new indices.
        /// </summary>
        public int NumberOfShards { get; set; } = 1;

        /// <summary>
        /// Gets or sets the number of replicas for new indices.
        /// </summary>
        public int NumberOfReplicas { get; set; } = 1;

        /// <summary>
        /// Gets or sets whether to send metrics to Elasticsearch.
        /// </summary>
        public bool SendMetrics { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to send logs to Elasticsearch.
        /// </summary>
        public bool SendLogs { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to send traces to Elasticsearch.
        /// </summary>
        public bool SendTraces { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to verify SSL certificates.
        /// </summary>
        public bool VerifySsl { get; set; } = true;
    }

    #endregion

    #region Document Types

    /// <summary>
    /// Represents a metric document for Elasticsearch.
    /// </summary>
    public sealed class MetricDocument
    {
        /// <summary>
        /// Gets or sets the timestamp.
        /// </summary>
        [JsonPropertyName("@timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets the metric name.
        /// </summary>
        [JsonPropertyName("metric_name")]
        public string MetricName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the metric type (counter, gauge, histogram, summary).
        /// </summary>
        [JsonPropertyName("metric_type")]
        public string MetricType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the metric value.
        /// </summary>
        [JsonPropertyName("value")]
        public double Value { get; set; }

        /// <summary>
        /// Gets or sets the labels/tags.
        /// </summary>
        [JsonPropertyName("labels")]
        public Dictionary<string, string> Labels { get; set; } = new();

        /// <summary>
        /// Gets or sets the service name.
        /// </summary>
        [JsonPropertyName("service")]
        public string Service { get; set; } = "datawarehouse";

        /// <summary>
        /// Gets or sets the environment (dev, staging, prod).
        /// </summary>
        [JsonPropertyName("environment")]
        public string Environment { get; set; } = "production";

        /// <summary>
        /// Gets or sets the host name.
        /// </summary>
        [JsonPropertyName("host")]
        public string Host { get; set; } = System.Environment.MachineName;
    }

    /// <summary>
    /// Represents a log document for Elasticsearch.
    /// </summary>
    public sealed class LogDocument
    {
        /// <summary>
        /// Gets or sets the timestamp.
        /// </summary>
        [JsonPropertyName("@timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets the log level (debug, info, warning, error, critical).
        /// </summary>
        [JsonPropertyName("level")]
        public string Level { get; set; } = "info";

        /// <summary>
        /// Gets or sets the log message.
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the logger name.
        /// </summary>
        [JsonPropertyName("logger")]
        public string Logger { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the service name.
        /// </summary>
        [JsonPropertyName("service")]
        public string Service { get; set; } = "datawarehouse";

        /// <summary>
        /// Gets or sets the environment.
        /// </summary>
        [JsonPropertyName("environment")]
        public string Environment { get; set; } = "production";

        /// <summary>
        /// Gets or sets the host name.
        /// </summary>
        [JsonPropertyName("host")]
        public string Host { get; set; } = System.Environment.MachineName;

        /// <summary>
        /// Gets or sets additional fields.
        /// </summary>
        [JsonPropertyName("fields")]
        public Dictionary<string, object> Fields { get; set; } = new();

        /// <summary>
        /// Gets or sets the exception information.
        /// </summary>
        [JsonPropertyName("exception")]
        public ExceptionInfo? Exception { get; set; }
    }

    /// <summary>
    /// Represents exception information in a log document.
    /// </summary>
    public sealed class ExceptionInfo
    {
        /// <summary>
        /// Gets or sets the exception type.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the exception message.
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the stack trace.
        /// </summary>
        [JsonPropertyName("stack_trace")]
        public string? StackTrace { get; set; }
    }

    #endregion

    #region Bulk API Types

    /// <summary>
    /// Represents a bulk operation action.
    /// </summary>
    public enum BulkActionType
    {
        /// <summary>
        /// Index a document (create or replace).
        /// </summary>
        Index,

        /// <summary>
        /// Create a document (fail if exists).
        /// </summary>
        Create,

        /// <summary>
        /// Update a document (partial update).
        /// </summary>
        Update,

        /// <summary>
        /// Delete a document.
        /// </summary>
        Delete
    }

    /// <summary>
    /// Represents a bulk operation.
    /// </summary>
    public sealed class BulkOperation
    {
        /// <summary>
        /// Gets or sets the bulk action type.
        /// </summary>
        public BulkActionType Action { get; set; }

        /// <summary>
        /// Gets or sets the index name.
        /// </summary>
        public string Index { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the document ID (optional).
        /// </summary>
        public string? Id { get; set; }

        /// <summary>
        /// Gets or sets the document to index.
        /// </summary>
        public object? Document { get; set; }
    }

    /// <summary>
    /// Represents a bulk API response.
    /// </summary>
    public sealed class BulkResponse
    {
        /// <summary>
        /// Gets or sets the time taken in milliseconds.
        /// </summary>
        [JsonPropertyName("took")]
        public int Took { get; set; }

        /// <summary>
        /// Gets or sets whether any errors occurred.
        /// </summary>
        [JsonPropertyName("errors")]
        public bool Errors { get; set; }

        /// <summary>
        /// Gets or sets the list of bulk item responses.
        /// </summary>
        [JsonPropertyName("items")]
        public List<BulkItemResponse>? Items { get; set; }
    }

    /// <summary>
    /// Represents a single item in a bulk response.
    /// </summary>
    public sealed class BulkItemResponse
    {
        /// <summary>
        /// Gets or sets the index operation details.
        /// </summary>
        [JsonPropertyName("index")]
        public BulkItemDetails? Index { get; set; }

        /// <summary>
        /// Gets or sets the create operation details.
        /// </summary>
        [JsonPropertyName("create")]
        public BulkItemDetails? Create { get; set; }

        /// <summary>
        /// Gets or sets the update operation details.
        /// </summary>
        [JsonPropertyName("update")]
        public BulkItemDetails? Update { get; set; }

        /// <summary>
        /// Gets or sets the delete operation details.
        /// </summary>
        [JsonPropertyName("delete")]
        public BulkItemDetails? Delete { get; set; }
    }

    /// <summary>
    /// Represents details of a bulk item response.
    /// </summary>
    public sealed class BulkItemDetails
    {
        /// <summary>
        /// Gets or sets the index name.
        /// </summary>
        [JsonPropertyName("_index")]
        public string Index { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the document ID.
        /// </summary>
        [JsonPropertyName("_id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the HTTP status code.
        /// </summary>
        [JsonPropertyName("status")]
        public int Status { get; set; }

        /// <summary>
        /// Gets or sets the error details (if any).
        /// </summary>
        [JsonPropertyName("error")]
        public BulkError? Error { get; set; }
    }

    /// <summary>
    /// Represents a bulk operation error.
    /// </summary>
    public sealed class BulkError
    {
        /// <summary>
        /// Gets or sets the error type.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the error reason/message.
        /// </summary>
        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Statistics for the Kibana plugin.
    /// </summary>
    public sealed class KibanaStatistics
    {
        /// <summary>
        /// Gets or sets the total number of documents sent.
        /// </summary>
        public long DocumentsSent { get; set; }

        /// <summary>
        /// Gets or sets the total number of documents failed.
        /// </summary>
        public long DocumentsFailed { get; set; }

        /// <summary>
        /// Gets or sets the total number of bulk requests.
        /// </summary>
        public long BulkRequests { get; set; }

        /// <summary>
        /// Gets or sets the total number of bulk errors.
        /// </summary>
        public long BulkErrors { get; set; }

        /// <summary>
        /// Gets or sets the number of documents currently buffered.
        /// </summary>
        public int BufferedDocuments { get; set; }

        /// <summary>
        /// Gets or sets the last flush timestamp.
        /// </summary>
        public DateTime? LastFlush { get; set; }

        /// <summary>
        /// Gets or sets the last error message.
        /// </summary>
        public string? LastError { get; set; }

        /// <summary>
        /// Gets or sets the last error timestamp.
        /// </summary>
        public DateTime? LastErrorTime { get; set; }
    }

    #endregion
}
