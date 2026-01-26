namespace DataWarehouse.Plugins.DatabaseImport
{
    /// <summary>
    /// Supported database types for import.
    /// </summary>
    public enum DatabaseType
    {
        SqlServer,
        PostgreSQL,
        MySQL,
        SQLite,
        Oracle,
        MongoDB
    }

    /// <summary>
    /// Configuration for a database import connection.
    /// </summary>
    public class ImportConfiguration
    {
        /// <summary>
        /// Type of database to import from.
        /// </summary>
        public DatabaseType DatabaseType { get; init; }

        /// <summary>
        /// Connection string for the source database.
        /// </summary>
        public string ConnectionString { get; init; } = string.Empty;

        /// <summary>
        /// Optional timeout for database operations (in seconds).
        /// </summary>
        public int TimeoutSeconds { get; init; } = 30;

        /// <summary>
        /// Whether to use SSL/TLS for connections.
        /// </summary>
        public bool UseSsl { get; init; } = true;

        /// <summary>
        /// Optional metadata to attach to imported objects.
        /// </summary>
        public Dictionary<string, string> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Result of an import operation.
    /// </summary>
    public class ImportResult
    {
        /// <summary>
        /// Whether the import was successful.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Number of records imported.
        /// </summary>
        public long RecordsImported { get; init; }

        /// <summary>
        /// Total bytes imported.
        /// </summary>
        public long BytesImported { get; init; }

        /// <summary>
        /// Duration of the import operation.
        /// </summary>
        public TimeSpan Duration { get; init; }

        /// <summary>
        /// Error message if not successful.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Names of collections/tables created.
        /// </summary>
        public List<string> CreatedCollections { get; init; } = new();

        /// <summary>
        /// Import statistics by table.
        /// </summary>
        public Dictionary<string, TableImportStats> TableStats { get; init; } = new();
    }

    /// <summary>
    /// Statistics for a single table import.
    /// </summary>
    public class TableImportStats
    {
        /// <summary>
        /// Table name.
        /// </summary>
        public string TableName { get; set; } = string.Empty;

        /// <summary>
        /// Number of rows imported.
        /// </summary>
        public long RowsImported { get; set; }

        /// <summary>
        /// Number of columns in the table.
        /// </summary>
        public int ColumnCount { get; set; }

        /// <summary>
        /// Total bytes for this table.
        /// </summary>
        public long BytesImported { get; set; }

        /// <summary>
        /// Errors encountered during import.
        /// </summary>
        public List<string> Errors { get; set; } = new();
    }

    /// <summary>
    /// Mapping configuration for a table import.
    /// </summary>
    public class TableMapping
    {
        /// <summary>
        /// Source schema name.
        /// </summary>
        public string? SourceSchema { get; init; }

        /// <summary>
        /// Source table name.
        /// </summary>
        public string SourceTable { get; init; } = string.Empty;

        /// <summary>
        /// Target collection name in DataWarehouse.
        /// </summary>
        public string TargetCollection { get; init; } = string.Empty;

        /// <summary>
        /// Column mappings (source column -> target field).
        /// If null, uses auto-mapping.
        /// </summary>
        public Dictionary<string, string>? ColumnMappings { get; init; }

        /// <summary>
        /// Optional WHERE clause for filtering rows.
        /// </summary>
        public string? WhereClause { get; init; }

        /// <summary>
        /// Maximum number of rows to import (0 = all).
        /// </summary>
        public long MaxRows { get; init; }

        /// <summary>
        /// Batch size for importing rows.
        /// </summary>
        public int BatchSize { get; init; } = 1000;

        /// <summary>
        /// Whether to track incremental imports (only import new/changed rows).
        /// </summary>
        public bool IncrementalMode { get; init; }

        /// <summary>
        /// Column to use as incremental timestamp/ID.
        /// </summary>
        public string? IncrementalColumn { get; init; }
    }

    /// <summary>
    /// Information about a discovered database schema.
    /// </summary>
    public class SchemaInfo
    {
        /// <summary>
        /// Schema name.
        /// </summary>
        public string SchemaName { get; init; } = string.Empty;

        /// <summary>
        /// Tables in this schema.
        /// </summary>
        public List<TableInfo> Tables { get; init; } = new();

        /// <summary>
        /// Total estimated size in bytes.
        /// </summary>
        public long EstimatedSizeBytes { get; init; }
    }

    /// <summary>
    /// Information about a discovered table.
    /// </summary>
    public class TableInfo
    {
        /// <summary>
        /// Schema name.
        /// </summary>
        public string? SchemaName { get; init; }

        /// <summary>
        /// Table name.
        /// </summary>
        public string TableName { get; init; } = string.Empty;

        /// <summary>
        /// Columns in this table.
        /// </summary>
        public List<ColumnInfo> Columns { get; init; } = new();

        /// <summary>
        /// Estimated row count.
        /// </summary>
        public long EstimatedRowCount { get; init; }

        /// <summary>
        /// Estimated size in bytes.
        /// </summary>
        public long EstimatedSizeBytes { get; init; }

        /// <summary>
        /// Primary key columns.
        /// </summary>
        public List<string> PrimaryKeyColumns { get; init; } = new();
    }

    /// <summary>
    /// Information about a table column.
    /// </summary>
    public class ColumnInfo
    {
        /// <summary>
        /// Column name.
        /// </summary>
        public string ColumnName { get; init; } = string.Empty;

        /// <summary>
        /// Data type.
        /// </summary>
        public string DataType { get; init; } = string.Empty;

        /// <summary>
        /// Whether the column is nullable.
        /// </summary>
        public bool IsNullable { get; init; }

        /// <summary>
        /// Whether this is a primary key column.
        /// </summary>
        public bool IsPrimaryKey { get; init; }

        /// <summary>
        /// Maximum length (for string types).
        /// </summary>
        public int? MaxLength { get; init; }

        /// <summary>
        /// Precision (for numeric types).
        /// </summary>
        public int? Precision { get; init; }

        /// <summary>
        /// Scale (for decimal types).
        /// </summary>
        public int? Scale { get; init; }
    }

    /// <summary>
    /// Status of an import session.
    /// </summary>
    public class ImportStatus
    {
        /// <summary>
        /// Import session ID.
        /// </summary>
        public string SessionId { get; set; } = string.Empty;

        /// <summary>
        /// Current state.
        /// </summary>
        public ImportState State { get; set; }

        /// <summary>
        /// Progress percentage (0-100).
        /// </summary>
        public double ProgressPercent { get; set; }

        /// <summary>
        /// Current table being imported.
        /// </summary>
        public string? CurrentTable { get; set; }

        /// <summary>
        /// Rows imported so far.
        /// </summary>
        public long RowsImported { get; set; }

        /// <summary>
        /// Total rows to import.
        /// </summary>
        public long TotalRows { get; set; }

        /// <summary>
        /// When the import started.
        /// </summary>
        public DateTime StartedAt { get; set; }

        /// <summary>
        /// Estimated completion time.
        /// </summary>
        public DateTime? EstimatedCompletion { get; set; }

        /// <summary>
        /// Error message if failed.
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Import state.
    /// </summary>
    public enum ImportState
    {
        NotStarted,
        Connecting,
        DiscoveringSchema,
        Importing,
        Completed,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Import history record.
    /// </summary>
    public class ImportHistoryRecord
    {
        /// <summary>
        /// Import session ID.
        /// </summary>
        public string SessionId { get; init; } = string.Empty;

        /// <summary>
        /// Database type.
        /// </summary>
        public DatabaseType DatabaseType { get; init; }

        /// <summary>
        /// Server/host name.
        /// </summary>
        public string ServerName { get; init; } = string.Empty;

        /// <summary>
        /// Database name.
        /// </summary>
        public string DatabaseName { get; init; } = string.Empty;

        /// <summary>
        /// When the import was performed.
        /// </summary>
        public DateTime ImportedAt { get; init; }

        /// <summary>
        /// Import result.
        /// </summary>
        public ImportResult Result { get; init; } = new();

        /// <summary>
        /// Last incremental import value (for incremental imports).
        /// </summary>
        public object? LastIncrementalValue { get; init; }
    }
}
