using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.Redash
{
    #region Configuration

    /// <summary>
    /// Configuration options for the Redash plugin.
    /// </summary>
    public sealed class RedashConfiguration
    {
        /// <summary>
        /// Gets or sets the Redash instance URL.
        /// </summary>
        public string RedashUrl { get; set; } = "http://localhost:5000";

        /// <summary>
        /// Gets or sets the API key for authentication.
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the default query timeout in seconds.
        /// </summary>
        public int QueryTimeoutSeconds { get; set; } = 300;

        /// <summary>
        /// Gets or sets the maximum number of concurrent requests.
        /// </summary>
        public int MaxConcurrentRequests { get; set; } = 10;

        /// <summary>
        /// Gets or sets whether to verify SSL certificates.
        /// </summary>
        public bool VerifySsl { get; set; } = true;

        /// <summary>
        /// Gets or sets the HTTP request timeout in seconds.
        /// </summary>
        public int RequestTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets whether to automatically refresh query results.
        /// </summary>
        public bool AutoRefreshResults { get; set; } = true;

        /// <summary>
        /// Gets or sets the default page size for paginated results.
        /// </summary>
        public int DefaultPageSize { get; set; } = 100;
    }

    #endregion

    #region Request/Response Models

    /// <summary>
    /// Represents a Redash query.
    /// </summary>
    public sealed class RedashQuery
    {
        /// <summary>
        /// Gets or sets the query ID.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the query name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the query description.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the query text.
        /// </summary>
        [JsonPropertyName("query")]
        public string QueryText { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the data source ID.
        /// </summary>
        [JsonPropertyName("data_source_id")]
        public int DataSourceId { get; set; }

        /// <summary>
        /// Gets or sets the created timestamp.
        /// </summary>
        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Gets or sets the updated timestamp.
        /// </summary>
        [JsonPropertyName("updated_at")]
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// Gets or sets whether the query is archived.
        /// </summary>
        [JsonPropertyName("is_archived")]
        public bool IsArchived { get; set; }

        /// <summary>
        /// Gets or sets the latest query result ID.
        /// </summary>
        [JsonPropertyName("latest_query_data_id")]
        public int? LatestQueryDataId { get; set; }

        /// <summary>
        /// Gets or sets the query tags.
        /// </summary>
        [JsonPropertyName("tags")]
        public List<string>? Tags { get; set; }
    }

    /// <summary>
    /// Represents a query execution request.
    /// </summary>
    public sealed class QueryExecutionRequest
    {
        /// <summary>
        /// Gets or sets the query ID to execute.
        /// </summary>
        [JsonPropertyName("query_id")]
        public int QueryId { get; set; }

        /// <summary>
        /// Gets or sets the maximum age of cached results in seconds.
        /// </summary>
        [JsonPropertyName("max_age")]
        public int? MaxAge { get; set; }

        /// <summary>
        /// Gets or sets the query parameters.
        /// </summary>
        [JsonPropertyName("parameters")]
        public Dictionary<string, object>? Parameters { get; set; }
    }

    /// <summary>
    /// Represents a query execution job.
    /// </summary>
    public sealed class QueryJob
    {
        /// <summary>
        /// Gets or sets the job ID.
        /// </summary>
        [JsonPropertyName("job")]
        public JobStatus Job { get; set; } = new();

        /// <summary>
        /// Gets or sets the query result.
        /// </summary>
        [JsonPropertyName("query_result")]
        public QueryResult? QueryResult { get; set; }
    }

    /// <summary>
    /// Represents a job status.
    /// </summary>
    public sealed class JobStatus
    {
        /// <summary>
        /// Gets or sets the job ID.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the job status.
        /// </summary>
        [JsonPropertyName("status")]
        public int Status { get; set; }

        /// <summary>
        /// Gets or sets the error message if failed.
        /// </summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        /// <summary>
        /// Gets or sets the updated timestamp.
        /// </summary>
        [JsonPropertyName("updated_at")]
        public long UpdatedAt { get; set; }

        /// <summary>
        /// Gets whether the job is complete.
        /// </summary>
        public bool IsComplete => Status == 3 || Status == 4;

        /// <summary>
        /// Gets whether the job succeeded.
        /// </summary>
        public bool IsSuccess => Status == 3;

        /// <summary>
        /// Gets whether the job failed.
        /// </summary>
        public bool IsFailed => Status == 4;
    }

    /// <summary>
    /// Represents query result data.
    /// </summary>
    public sealed class QueryResult
    {
        /// <summary>
        /// Gets or sets the result ID.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the query hash.
        /// </summary>
        [JsonPropertyName("query_hash")]
        public string? QueryHash { get; set; }

        /// <summary>
        /// Gets or sets the query text.
        /// </summary>
        [JsonPropertyName("query")]
        public string? Query { get; set; }

        /// <summary>
        /// Gets or sets the data source ID.
        /// </summary>
        [JsonPropertyName("data_source_id")]
        public int DataSourceId { get; set; }

        /// <summary>
        /// Gets or sets the result data.
        /// </summary>
        [JsonPropertyName("data")]
        public ResultData? Data { get; set; }

        /// <summary>
        /// Gets or sets the runtime in seconds.
        /// </summary>
        [JsonPropertyName("runtime")]
        public double Runtime { get; set; }

        /// <summary>
        /// Gets or sets the retrieved timestamp.
        /// </summary>
        [JsonPropertyName("retrieved_at")]
        public DateTime RetrievedAt { get; set; }
    }

    /// <summary>
    /// Represents the result data structure.
    /// </summary>
    public sealed class ResultData
    {
        /// <summary>
        /// Gets or sets the column definitions.
        /// </summary>
        [JsonPropertyName("columns")]
        public List<Column>? Columns { get; set; }

        /// <summary>
        /// Gets or sets the result rows.
        /// </summary>
        [JsonPropertyName("rows")]
        public List<Dictionary<string, object>>? Rows { get; set; }
    }

    /// <summary>
    /// Represents a column definition.
    /// </summary>
    public sealed class Column
    {
        /// <summary>
        /// Gets or sets the column name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the column type.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the friendly name.
        /// </summary>
        [JsonPropertyName("friendly_name")]
        public string? FriendlyName { get; set; }
    }

    /// <summary>
    /// Represents a Redash visualization.
    /// </summary>
    public sealed class RedashVisualization
    {
        /// <summary>
        /// Gets or sets the visualization ID.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the visualization type.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the visualization name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the visualization description.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the query ID.
        /// </summary>
        [JsonPropertyName("query_id")]
        public int QueryId { get; set; }

        /// <summary>
        /// Gets or sets the visualization options.
        /// </summary>
        [JsonPropertyName("options")]
        public Dictionary<string, object>? Options { get; set; }

        /// <summary>
        /// Gets or sets the created timestamp.
        /// </summary>
        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Gets or sets the updated timestamp.
        /// </summary>
        [JsonPropertyName("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// Represents a Redash dashboard.
    /// </summary>
    public sealed class RedashDashboard
    {
        /// <summary>
        /// Gets or sets the dashboard ID.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the dashboard name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the dashboard slug.
        /// </summary>
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        /// <summary>
        /// Gets or sets the created timestamp.
        /// </summary>
        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Gets or sets the updated timestamp.
        /// </summary>
        [JsonPropertyName("updated_at")]
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// Gets or sets whether the dashboard is published.
        /// </summary>
        [JsonPropertyName("is_draft")]
        public bool IsDraft { get; set; }

        /// <summary>
        /// Gets or sets the dashboard tags.
        /// </summary>
        [JsonPropertyName("tags")]
        public List<string>? Tags { get; set; }

        /// <summary>
        /// Gets or sets the dashboard widgets.
        /// </summary>
        [JsonPropertyName("widgets")]
        public List<DashboardWidget>? Widgets { get; set; }
    }

    /// <summary>
    /// Represents a dashboard widget.
    /// </summary>
    public sealed class DashboardWidget
    {
        /// <summary>
        /// Gets or sets the widget ID.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the visualization ID.
        /// </summary>
        [JsonPropertyName("visualization_id")]
        public int? VisualizationId { get; set; }

        /// <summary>
        /// Gets or sets the widget text (for text widgets).
        /// </summary>
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        /// <summary>
        /// Gets or sets the widget options.
        /// </summary>
        [JsonPropertyName("options")]
        public WidgetOptions? Options { get; set; }
    }

    /// <summary>
    /// Represents widget layout options.
    /// </summary>
    public sealed class WidgetOptions
    {
        /// <summary>
        /// Gets or sets the position configuration.
        /// </summary>
        [JsonPropertyName("position")]
        public WidgetPosition? Position { get; set; }
    }

    /// <summary>
    /// Represents widget position.
    /// </summary>
    public sealed class WidgetPosition
    {
        /// <summary>
        /// Gets or sets the column index.
        /// </summary>
        [JsonPropertyName("col")]
        public int Col { get; set; }

        /// <summary>
        /// Gets or sets the row index.
        /// </summary>
        [JsonPropertyName("row")]
        public int Row { get; set; }

        /// <summary>
        /// Gets or sets the column span.
        /// </summary>
        [JsonPropertyName("sizeX")]
        public int SizeX { get; set; }

        /// <summary>
        /// Gets or sets the row span.
        /// </summary>
        [JsonPropertyName("sizeY")]
        public int SizeY { get; set; }
    }

    /// <summary>
    /// Represents a data source.
    /// </summary>
    public sealed class RedashDataSource
    {
        /// <summary>
        /// Gets or sets the data source ID.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the data source name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the data source type.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the syntax highlighting mode.
        /// </summary>
        [JsonPropertyName("syntax")]
        public string? Syntax { get; set; }
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Statistics for the Redash plugin.
    /// </summary>
    public sealed class RedashStatistics
    {
        /// <summary>
        /// Gets or sets the total number of queries executed.
        /// </summary>
        public long TotalQueriesExecuted { get; set; }

        /// <summary>
        /// Gets or sets the total number of successful queries.
        /// </summary>
        public long SuccessfulQueries { get; set; }

        /// <summary>
        /// Gets or sets the total number of failed queries.
        /// </summary>
        public long FailedQueries { get; set; }

        /// <summary>
        /// Gets or sets the total query execution time in seconds.
        /// </summary>
        public double TotalExecutionTime { get; set; }

        /// <summary>
        /// Gets or sets the average query execution time in seconds.
        /// </summary>
        public double AverageExecutionTime => TotalQueriesExecuted > 0 ? TotalExecutionTime / TotalQueriesExecuted : 0;

        /// <summary>
        /// Gets or sets the number of API requests made.
        /// </summary>
        public long ApiRequestCount { get; set; }

        /// <summary>
        /// Gets or sets the number of API errors.
        /// </summary>
        public long ApiErrorCount { get; set; }

        /// <summary>
        /// Gets or sets the last query execution time.
        /// </summary>
        public DateTime? LastQueryTime { get; set; }
    }

    #endregion
}
