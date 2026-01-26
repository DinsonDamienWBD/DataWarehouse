using System;
using System.Collections.Generic;

namespace DataWarehouse.Plugins.FederatedQuery
{
    /// <summary>
    /// Unified federated query that can span multiple data sources.
    /// </summary>
    public sealed class FederatedQuery
    {
        /// <summary>
        /// SQL-like query text for unified querying.
        /// </summary>
        public string QueryText { get; set; } = string.Empty;

        /// <summary>
        /// Data sources to query. If empty, queries all available sources.
        /// </summary>
        public List<string> TargetSources { get; set; } = new();

        /// <summary>
        /// Maximum number of results to return.
        /// </summary>
        public int Limit { get; set; } = 100;

        /// <summary>
        /// Whether to include file metadata in results.
        /// </summary>
        public bool IncludeFileMetadata { get; set; } = true;

        /// <summary>
        /// Whether to include database records in results.
        /// </summary>
        public bool IncludeRecords { get; set; } = true;

        /// <summary>
        /// Whether to include file content search.
        /// </summary>
        public bool IncludeFileContent { get; set; }

        /// <summary>
        /// Filters to apply across all sources.
        /// </summary>
        public Dictionary<string, object> Filters { get; set; } = new();

        /// <summary>
        /// Date range filter (created/modified after this date).
        /// </summary>
        public DateTime? DateFrom { get; set; }

        /// <summary>
        /// Date range filter (created/modified before this date).
        /// </summary>
        public DateTime? DateTo { get; set; }
    }

    /// <summary>
    /// Result of a federated query.
    /// </summary>
    public sealed class QueryResult
    {
        /// <summary>
        /// Unified result set combining all sources.
        /// </summary>
        public UnifiedResultSet ResultSet { get; init; } = new();

        /// <summary>
        /// Query execution time.
        /// </summary>
        public TimeSpan ExecutionTime { get; init; }

        /// <summary>
        /// Whether the query was successful.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Error message if query failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Query execution plan (for debugging).
        /// </summary>
        public QueryExecutionPlan? ExecutionPlan { get; init; }

        /// <summary>
        /// Statistics about the query execution.
        /// </summary>
        public QueryStatistics Statistics { get; init; } = new();
    }

    /// <summary>
    /// Unified result set combining data from multiple sources.
    /// </summary>
    public sealed class UnifiedResultSet
    {
        /// <summary>
        /// Combined results from all sources.
        /// </summary>
        public List<UnifiedResult> Results { get; init; } = new();

        /// <summary>
        /// Total number of results (before limit applied).
        /// </summary>
        public int TotalCount { get; init; }

        /// <summary>
        /// Number of sources queried.
        /// </summary>
        public int SourcesQueried { get; init; }

        /// <summary>
        /// Breakdown of results by source type.
        /// </summary>
        public Dictionary<DataSourceType, int> ResultsBySource { get; init; } = new();
    }

    /// <summary>
    /// A single unified result from any data source.
    /// </summary>
    public sealed class UnifiedResult
    {
        /// <summary>
        /// Unique identifier for this result.
        /// </summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>
        /// Type of data source this result came from.
        /// </summary>
        public DataSourceType SourceType { get; init; }

        /// <summary>
        /// Logical name of the data source.
        /// </summary>
        public string SourceName { get; init; } = string.Empty;

        /// <summary>
        /// Relevance score (0-1).
        /// </summary>
        public double Score { get; init; }

        /// <summary>
        /// Display name/title for this result.
        /// </summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>
        /// Optional snippet/summary of the result.
        /// </summary>
        public string? Snippet { get; init; }

        /// <summary>
        /// Metadata fields for this result.
        /// </summary>
        public Dictionary<string, object> Metadata { get; init; } = new();

        /// <summary>
        /// Raw data (for structured results like database rows).
        /// </summary>
        public Dictionary<string, object>? Data { get; init; }

        /// <summary>
        /// Created/indexed timestamp.
        /// </summary>
        public DateTime? CreatedAt { get; init; }

        /// <summary>
        /// Modified timestamp.
        /// </summary>
        public DateTime? ModifiedAt { get; init; }
    }

    /// <summary>
    /// Represents a registered data source in the warehouse.
    /// </summary>
    public sealed class DataSource
    {
        /// <summary>
        /// Logical name of the data source.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Type of data source.
        /// </summary>
        public DataSourceType Type { get; init; }

        /// <summary>
        /// Whether this source is currently available.
        /// </summary>
        public bool IsAvailable { get; init; }

        /// <summary>
        /// Schema information for structured sources.
        /// </summary>
        public DataSourceSchema? Schema { get; init; }

        /// <summary>
        /// Number of records/items in this source.
        /// </summary>
        public long RecordCount { get; init; }

        /// <summary>
        /// Additional metadata about the source.
        /// </summary>
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Schema information for a data source.
    /// </summary>
    public sealed class DataSourceSchema
    {
        /// <summary>
        /// Available fields/columns.
        /// </summary>
        public List<SchemaField> Fields { get; init; } = new();

        /// <summary>
        /// Whether this source supports full-text search.
        /// </summary>
        public bool SupportsFullText { get; init; }

        /// <summary>
        /// Whether this source supports filtering.
        /// </summary>
        public bool SupportsFiltering { get; init; }

        /// <summary>
        /// Whether this source supports joins.
        /// </summary>
        public bool SupportsJoins { get; init; }
    }

    /// <summary>
    /// Represents a field in a data source schema.
    /// </summary>
    public sealed class SchemaField
    {
        /// <summary>
        /// Field name.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Field data type.
        /// </summary>
        public string DataType { get; init; } = string.Empty;

        /// <summary>
        /// Whether this field is indexed.
        /// </summary>
        public bool IsIndexed { get; init; }

        /// <summary>
        /// Whether this field is searchable.
        /// </summary>
        public bool IsSearchable { get; init; }

        /// <summary>
        /// Whether this field can be used for filtering.
        /// </summary>
        public bool IsFilterable { get; init; }
    }

    /// <summary>
    /// Type of data source.
    /// </summary>
    public enum DataSourceType
    {
        /// <summary>
        /// File metadata (names, paths, properties).
        /// </summary>
        FileMetadata,

        /// <summary>
        /// File content (full-text search).
        /// </summary>
        FileContent,

        /// <summary>
        /// Database collection/table.
        /// </summary>
        DatabaseCollection,

        /// <summary>
        /// JSON/structured object store.
        /// </summary>
        ObjectStore,

        /// <summary>
        /// External database connection.
        /// </summary>
        ExternalDatabase
    }

    /// <summary>
    /// Query execution plan for debugging.
    /// </summary>
    public sealed class QueryExecutionPlan
    {
        /// <summary>
        /// Parsed query components.
        /// </summary>
        public List<string> QuerySteps { get; set; } = new();

        /// <summary>
        /// Sources that will be queried.
        /// </summary>
        public List<string> TargetSources { get; set; } = new();

        /// <summary>
        /// Estimated cost/complexity.
        /// </summary>
        public double EstimatedCost { get; set; }

        /// <summary>
        /// Whether the query uses indexes.
        /// </summary>
        public bool UsesIndexes { get; set; }

        /// <summary>
        /// Warnings or optimization suggestions.
        /// </summary>
        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>
    /// Statistics about query execution.
    /// </summary>
    public sealed class QueryStatistics
    {
        /// <summary>
        /// Number of sources queried.
        /// </summary>
        public int SourcesQueried { get; set; }

        /// <summary>
        /// Number of records scanned across all sources.
        /// </summary>
        public long RecordsScanned { get; set; }

        /// <summary>
        /// Number of results returned.
        /// </summary>
        public int ResultsReturned { get; set; }

        /// <summary>
        /// Time spent in each source.
        /// </summary>
        public Dictionary<string, TimeSpan> TimeBySource { get; set; } = new();

        /// <summary>
        /// Whether any caches were used.
        /// </summary>
        public bool CacheHit { get; set; }
    }
}
