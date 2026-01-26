using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.Metabase
{
    #region Configuration

    /// <summary>
    /// Configuration options for the Metabase plugin.
    /// </summary>
    public sealed class MetabaseConfiguration
    {
        /// <summary>
        /// Gets or sets the Metabase server URL.
        /// </summary>
        public string MetabaseUrl { get; set; } = "http://localhost:3000";

        /// <summary>
        /// Gets or sets the username for authentication.
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the password for authentication.
        /// </summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the request timeout in seconds.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets whether to verify SSL certificates.
        /// </summary>
        public bool VerifySsl { get; set; } = true;

        /// <summary>
        /// Gets or sets the session refresh interval in minutes.
        /// </summary>
        public int SessionRefreshIntervalMinutes { get; set; } = 10;

        /// <summary>
        /// Gets or sets the maximum number of retry attempts.
        /// </summary>
        public int MaxRetryAttempts { get; set; } = 3;

        /// <summary>
        /// Gets or sets the retry delay in milliseconds.
        /// </summary>
        public int RetryDelayMs { get; set; } = 1000;
    }

    #endregion

    #region Authentication

    /// <summary>
    /// Request model for Metabase authentication.
    /// </summary>
    internal sealed class LoginRequest
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;
    }

    /// <summary>
    /// Response model for Metabase authentication.
    /// </summary>
    internal sealed class LoginResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    #endregion

    #region Questions

    /// <summary>
    /// Represents a Metabase question (saved query).
    /// </summary>
    public sealed class MetabaseQuestion
    {
        /// <summary>
        /// Gets or sets the question ID.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the question name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the question description.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the database ID.
        /// </summary>
        [JsonPropertyName("database_id")]
        public int DatabaseId { get; set; }

        /// <summary>
        /// Gets or sets the table ID.
        /// </summary>
        [JsonPropertyName("table_id")]
        public int? TableId { get; set; }

        /// <summary>
        /// Gets or sets the query definition.
        /// </summary>
        [JsonPropertyName("dataset_query")]
        public object? DatasetQuery { get; set; }

        /// <summary>
        /// Gets or sets the display type.
        /// </summary>
        [JsonPropertyName("display")]
        public string? Display { get; set; }

        /// <summary>
        /// Gets or sets the visualization settings.
        /// </summary>
        [JsonPropertyName("visualization_settings")]
        public object? VisualizationSettings { get; set; }

        /// <summary>
        /// Gets or sets the collection ID.
        /// </summary>
        [JsonPropertyName("collection_id")]
        public int? CollectionId { get; set; }

        /// <summary>
        /// Gets or sets the creator ID.
        /// </summary>
        [JsonPropertyName("creator_id")]
        public int? CreatorId { get; set; }

        /// <summary>
        /// Gets or sets when the question was created.
        /// </summary>
        [JsonPropertyName("created_at")]
        public DateTime? CreatedAt { get; set; }

        /// <summary>
        /// Gets or sets when the question was last updated.
        /// </summary>
        [JsonPropertyName("updated_at")]
        public DateTime? UpdatedAt { get; set; }
    }

    /// <summary>
    /// Request model for executing a question.
    /// </summary>
    internal sealed class QueryRequest
    {
        [JsonPropertyName("database")]
        public int? DatabaseId { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = "query";

        [JsonPropertyName("query")]
        public object? Query { get; set; }

        [JsonPropertyName("parameters")]
        public List<QueryParameter>? Parameters { get; set; }
    }

    /// <summary>
    /// Represents a query parameter.
    /// </summary>
    public sealed class QueryParameter
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "category";

        [JsonPropertyName("target")]
        public object? Target { get; set; }

        [JsonPropertyName("value")]
        public object? Value { get; set; }
    }

    /// <summary>
    /// Response model for query execution.
    /// </summary>
    public sealed class QueryResult
    {
        /// <summary>
        /// Gets or sets the query status.
        /// </summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the result data.
        /// </summary>
        [JsonPropertyName("data")]
        public QueryResultData? Data { get; set; }

        /// <summary>
        /// Gets or sets the error message if any.
        /// </summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    /// <summary>
    /// Represents the data portion of a query result.
    /// </summary>
    public sealed class QueryResultData
    {
        /// <summary>
        /// Gets or sets the column definitions.
        /// </summary>
        [JsonPropertyName("cols")]
        public List<ColumnDefinition>? Columns { get; set; }

        /// <summary>
        /// Gets or sets the result rows.
        /// </summary>
        [JsonPropertyName("rows")]
        public List<List<object?>>? Rows { get; set; }

        /// <summary>
        /// Gets or sets the number of rows.
        /// </summary>
        [JsonPropertyName("rows_truncated")]
        public int? RowsTruncated { get; set; }
    }

    /// <summary>
    /// Represents a column definition in query results.
    /// </summary>
    public sealed class ColumnDefinition
    {
        /// <summary>
        /// Gets or sets the column name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the display name.
        /// </summary>
        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the base type.
        /// </summary>
        [JsonPropertyName("base_type")]
        public string? BaseType { get; set; }

        /// <summary>
        /// Gets or sets the effective type.
        /// </summary>
        [JsonPropertyName("effective_type")]
        public string? EffectiveType { get; set; }

        /// <summary>
        /// Gets or sets the field reference.
        /// </summary>
        [JsonPropertyName("field_ref")]
        public object? FieldRef { get; set; }
    }

    #endregion

    #region Dashboards

    /// <summary>
    /// Represents a Metabase dashboard.
    /// </summary>
    public sealed class MetabaseDashboard
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
        /// Gets or sets the dashboard description.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the dashboard cards (questions).
        /// </summary>
        [JsonPropertyName("dashcards")]
        public List<DashboardCard>? DashboardCards { get; set; }

        /// <summary>
        /// Gets or sets the collection ID.
        /// </summary>
        [JsonPropertyName("collection_id")]
        public int? CollectionId { get; set; }

        /// <summary>
        /// Gets or sets the creator ID.
        /// </summary>
        [JsonPropertyName("creator_id")]
        public int? CreatorId { get; set; }

        /// <summary>
        /// Gets or sets when the dashboard was created.
        /// </summary>
        [JsonPropertyName("created_at")]
        public DateTime? CreatedAt { get; set; }

        /// <summary>
        /// Gets or sets when the dashboard was last updated.
        /// </summary>
        [JsonPropertyName("updated_at")]
        public DateTime? UpdatedAt { get; set; }
    }

    /// <summary>
    /// Represents a card on a dashboard.
    /// </summary>
    public sealed class DashboardCard
    {
        /// <summary>
        /// Gets or sets the card ID.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the card question.
        /// </summary>
        [JsonPropertyName("card")]
        public MetabaseQuestion? Card { get; set; }

        /// <summary>
        /// Gets or sets the parameter mappings.
        /// </summary>
        [JsonPropertyName("parameter_mappings")]
        public List<object>? ParameterMappings { get; set; }

        /// <summary>
        /// Gets or sets the size and position.
        /// </summary>
        [JsonPropertyName("size_x")]
        public int? SizeX { get; set; }

        /// <summary>
        /// Gets or sets the size and position.
        /// </summary>
        [JsonPropertyName("size_y")]
        public int? SizeY { get; set; }

        /// <summary>
        /// Gets or sets the row position.
        /// </summary>
        [JsonPropertyName("row")]
        public int? Row { get; set; }

        /// <summary>
        /// Gets or sets the column position.
        /// </summary>
        [JsonPropertyName("col")]
        public int? Col { get; set; }
    }

    #endregion

    #region Databases

    /// <summary>
    /// Represents a Metabase database connection.
    /// </summary>
    public sealed class MetabaseDatabase
    {
        /// <summary>
        /// Gets or sets the database ID.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the database name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the database engine.
        /// </summary>
        [JsonPropertyName("engine")]
        public string? Engine { get; set; }

        /// <summary>
        /// Gets or sets whether the database is active.
        /// </summary>
        [JsonPropertyName("is_sample")]
        public bool IsSample { get; set; }

        /// <summary>
        /// Gets or sets when the database was created.
        /// </summary>
        [JsonPropertyName("created_at")]
        public DateTime? CreatedAt { get; set; }

        /// <summary>
        /// Gets or sets when the database was last updated.
        /// </summary>
        [JsonPropertyName("updated_at")]
        public DateTime? UpdatedAt { get; set; }
    }

    #endregion

    #region Collections

    /// <summary>
    /// Represents a Metabase collection.
    /// </summary>
    public sealed class MetabaseCollection
    {
        /// <summary>
        /// Gets or sets the collection ID.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the collection name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the collection description.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the collection slug.
        /// </summary>
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        /// <summary>
        /// Gets or sets the collection color.
        /// </summary>
        [JsonPropertyName("color")]
        public string? Color { get; set; }

        /// <summary>
        /// Gets or sets whether this is a personal collection.
        /// </summary>
        [JsonPropertyName("personal_owner_id")]
        public int? PersonalOwnerId { get; set; }
    }

    #endregion

    #region Error Response

    /// <summary>
    /// Represents a Metabase API error response.
    /// </summary>
    internal sealed class ErrorResponse
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("errors")]
        public Dictionary<string, string>? Errors { get; set; }
    }

    #endregion
}
