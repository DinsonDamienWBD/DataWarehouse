using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.ApacheSuperset
{
    #region Configuration

    /// <summary>
    /// Configuration options for the Apache Superset plugin.
    /// </summary>
    public sealed class SupersetConfiguration
    {
        /// <summary>
        /// Gets or sets the Superset server URL.
        /// </summary>
        public string SupersetUrl { get; set; } = "http://localhost:8088";

        /// <summary>
        /// Gets or sets the authentication username.
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the authentication password.
        /// </summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the authentication provider (db, ldap, oauth, etc.).
        /// </summary>
        public string Provider { get; set; } = "db";

        /// <summary>
        /// Gets or sets whether to refresh the token automatically.
        /// </summary>
        public bool AutoRefreshToken { get; set; } = true;

        /// <summary>
        /// Gets or sets the token refresh interval in minutes.
        /// </summary>
        public int TokenRefreshIntervalMinutes { get; set; } = 30;

        /// <summary>
        /// Gets or sets the HTTP request timeout in seconds.
        /// </summary>
        public int RequestTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets whether to validate SSL certificates.
        /// </summary>
        public bool ValidateSslCertificate { get; set; } = true;

        /// <summary>
        /// Gets or sets the default database ID for datasets.
        /// </summary>
        public int? DefaultDatabaseId { get; set; }

        /// <summary>
        /// Gets or sets whether to enable caching for queries.
        /// </summary>
        public bool EnableCaching { get; set; } = true;

        /// <summary>
        /// Gets or sets the default cache timeout in seconds.
        /// </summary>
        public int CacheTimeoutSeconds { get; set; } = 300;
    }

    #endregion

    #region Authentication Types

    /// <summary>
    /// Request payload for Superset authentication.
    /// </summary>
    public sealed class LoginRequest
    {
        /// <summary>
        /// Gets or sets the username.
        /// </summary>
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the password.
        /// </summary>
        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the authentication provider.
        /// </summary>
        [JsonPropertyName("provider")]
        public string Provider { get; set; } = "db";

        /// <summary>
        /// Gets or sets whether to refresh the token.
        /// </summary>
        [JsonPropertyName("refresh")]
        public bool Refresh { get; set; } = true;
    }

    /// <summary>
    /// Response from Superset authentication.
    /// </summary>
    public sealed class LoginResponse
    {
        /// <summary>
        /// Gets or sets the access token (JWT).
        /// </summary>
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        /// <summary>
        /// Gets or sets the refresh token.
        /// </summary>
        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }
    }

    /// <summary>
    /// Request to refresh an access token.
    /// </summary>
    public sealed class RefreshTokenRequest
    {
        /// <summary>
        /// Gets or sets the refresh token.
        /// </summary>
        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty;
    }

    #endregion

    #region Dataset Types

    /// <summary>
    /// Represents a dataset (table) in Superset.
    /// </summary>
    public sealed class Dataset
    {
        /// <summary>
        /// Gets or sets the dataset ID.
        /// </summary>
        [JsonPropertyName("id")]
        public int? Id { get; set; }

        /// <summary>
        /// Gets or sets the table name.
        /// </summary>
        [JsonPropertyName("table_name")]
        public string TableName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the database ID.
        /// </summary>
        [JsonPropertyName("database")]
        public int DatabaseId { get; set; }

        /// <summary>
        /// Gets or sets the schema name.
        /// </summary>
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        /// <summary>
        /// Gets or sets the SQL query (for virtual datasets).
        /// </summary>
        [JsonPropertyName("sql")]
        public string? Sql { get; set; }

        /// <summary>
        /// Gets or sets the dataset description.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets whether the dataset is managed externally.
        /// </summary>
        [JsonPropertyName("is_managed_externally")]
        public bool IsManagedExternally { get; set; }

        /// <summary>
        /// Gets or sets the cache timeout in seconds.
        /// </summary>
        [JsonPropertyName("cache_timeout")]
        public int? CacheTimeout { get; set; }

        /// <summary>
        /// Gets or sets the dataset columns.
        /// </summary>
        [JsonPropertyName("columns")]
        public List<DatasetColumn>? Columns { get; set; }

        /// <summary>
        /// Gets or sets the dataset metrics.
        /// </summary>
        [JsonPropertyName("metrics")]
        public List<DatasetMetric>? Metrics { get; set; }
    }

    /// <summary>
    /// Represents a column in a dataset.
    /// </summary>
    public sealed class DatasetColumn
    {
        /// <summary>
        /// Gets or sets the column ID.
        /// </summary>
        [JsonPropertyName("id")]
        public int? Id { get; set; }

        /// <summary>
        /// Gets or sets the column name.
        /// </summary>
        [JsonPropertyName("column_name")]
        public string ColumnName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether the column is dimensional.
        /// </summary>
        [JsonPropertyName("is_dttm")]
        public bool IsDateTime { get; set; }

        /// <summary>
        /// Gets or sets whether the column is active.
        /// </summary>
        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Gets or sets the column type.
        /// </summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        /// <summary>
        /// Gets or sets whether the column is filterable.
        /// </summary>
        [JsonPropertyName("filterable")]
        public bool Filterable { get; set; } = true;

        /// <summary>
        /// Gets or sets whether the column can be grouped by.
        /// </summary>
        [JsonPropertyName("groupby")]
        public bool GroupBy { get; set; } = true;

        /// <summary>
        /// Gets or sets the column description.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the column expression (for calculated columns).
        /// </summary>
        [JsonPropertyName("expression")]
        public string? Expression { get; set; }
    }

    /// <summary>
    /// Represents a metric in a dataset.
    /// </summary>
    public sealed class DatasetMetric
    {
        /// <summary>
        /// Gets or sets the metric ID.
        /// </summary>
        [JsonPropertyName("id")]
        public int? Id { get; set; }

        /// <summary>
        /// Gets or sets the metric name.
        /// </summary>
        [JsonPropertyName("metric_name")]
        public string MetricName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the metric expression (SQL).
        /// </summary>
        [JsonPropertyName("expression")]
        public string Expression { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the metric type (e.g., "count", "sum", "avg").
        /// </summary>
        [JsonPropertyName("metric_type")]
        public string? MetricType { get; set; }

        /// <summary>
        /// Gets or sets the metric description.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets whether the metric is active.
        /// </summary>
        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; } = true;
    }

    #endregion

    #region Chart Types

    /// <summary>
    /// Represents a chart (slice) in Superset.
    /// </summary>
    public sealed class Chart
    {
        /// <summary>
        /// Gets or sets the chart ID.
        /// </summary>
        [JsonPropertyName("id")]
        public int? Id { get; set; }

        /// <summary>
        /// Gets or sets the chart name.
        /// </summary>
        [JsonPropertyName("slice_name")]
        public string SliceName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the visualization type.
        /// </summary>
        [JsonPropertyName("viz_type")]
        public string VizType { get; set; } = "table";

        /// <summary>
        /// Gets or sets the dataset ID.
        /// </summary>
        [JsonPropertyName("datasource_id")]
        public int DatasourceId { get; set; }

        /// <summary>
        /// Gets or sets the datasource type (usually "table").
        /// </summary>
        [JsonPropertyName("datasource_type")]
        public string DatasourceType { get; set; } = "table";

        /// <summary>
        /// Gets or sets the chart description.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the chart parameters (JSON).
        /// </summary>
        [JsonPropertyName("params")]
        public string? Params { get; set; }

        /// <summary>
        /// Gets or sets the cache timeout in seconds.
        /// </summary>
        [JsonPropertyName("cache_timeout")]
        public int? CacheTimeout { get; set; }
    }

    #endregion

    #region Dashboard Types

    /// <summary>
    /// Represents a dashboard in Superset.
    /// </summary>
    public sealed class Dashboard
    {
        /// <summary>
        /// Gets or sets the dashboard ID.
        /// </summary>
        [JsonPropertyName("id")]
        public int? Id { get; set; }

        /// <summary>
        /// Gets or sets the dashboard title.
        /// </summary>
        [JsonPropertyName("dashboard_title")]
        public string DashboardTitle { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the position JSON (layout).
        /// </summary>
        [JsonPropertyName("position_json")]
        public string? PositionJson { get; set; }

        /// <summary>
        /// Gets or sets the dashboard description.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the CSS for the dashboard.
        /// </summary>
        [JsonPropertyName("css")]
        public string? Css { get; set; }

        /// <summary>
        /// Gets or sets whether the dashboard is published.
        /// </summary>
        [JsonPropertyName("published")]
        public bool Published { get; set; } = true;

        /// <summary>
        /// Gets or sets the slice IDs to include in the dashboard.
        /// </summary>
        [JsonPropertyName("slices")]
        public List<int>? Slices { get; set; }
    }

    #endregion

    #region Response Types

    /// <summary>
    /// Generic response wrapper from Superset API.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    public sealed class SupersetResponse<T>
    {
        /// <summary>
        /// Gets or sets the result data.
        /// </summary>
        [JsonPropertyName("result")]
        public T? Result { get; set; }

        /// <summary>
        /// Gets or sets the result count.
        /// </summary>
        [JsonPropertyName("count")]
        public int Count { get; set; }

        /// <summary>
        /// Gets or sets the result IDs.
        /// </summary>
        [JsonPropertyName("ids")]
        public List<int>? Ids { get; set; }
    }

    /// <summary>
    /// Error response from Superset API.
    /// </summary>
    public sealed class SupersetErrorResponse
    {
        /// <summary>
        /// Gets or sets the error message.
        /// </summary>
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        /// <summary>
        /// Gets or sets the error details.
        /// </summary>
        [JsonPropertyName("errors")]
        public List<ErrorDetail>? Errors { get; set; }
    }

    /// <summary>
    /// Detailed error information.
    /// </summary>
    public sealed class ErrorDetail
    {
        /// <summary>
        /// Gets or sets the error message.
        /// </summary>
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        /// <summary>
        /// Gets or sets the error level.
        /// </summary>
        [JsonPropertyName("level")]
        public string? Level { get; set; }

        /// <summary>
        /// Gets or sets additional error information.
        /// </summary>
        [JsonPropertyName("extra")]
        public Dictionary<string, object>? Extra { get; set; }
    }

    #endregion

    #region Query Types

    /// <summary>
    /// Request to execute a SQL query in Superset.
    /// </summary>
    public sealed class SqlQueryRequest
    {
        /// <summary>
        /// Gets or sets the database ID.
        /// </summary>
        [JsonPropertyName("database_id")]
        public int DatabaseId { get; set; }

        /// <summary>
        /// Gets or sets the SQL query.
        /// </summary>
        [JsonPropertyName("sql")]
        public string Sql { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the schema name.
        /// </summary>
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        /// <summary>
        /// Gets or sets the client ID for tracking.
        /// </summary>
        [JsonPropertyName("client_id")]
        public string? ClientId { get; set; }

        /// <summary>
        /// Gets or sets the query limit.
        /// </summary>
        [JsonPropertyName("queryLimit")]
        public int? QueryLimit { get; set; }

        /// <summary>
        /// Gets or sets the template parameters.
        /// </summary>
        [JsonPropertyName("templateParams")]
        public string? TemplateParams { get; set; }
    }

    #endregion
}
