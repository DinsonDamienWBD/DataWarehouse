using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.SchemaRegistry
{
    /// <summary>
    /// Represents a complete schema definition for a table.
    /// </summary>
    public sealed class SchemaDefinition
    {
        /// <summary>
        /// Unique schema identifier (e.g., "SqlServer:AdventureWorks.invoices").
        /// </summary>
        public required string SchemaId { get; set; }

        /// <summary>
        /// Schema name (table name).
        /// </summary>
        public required string Name { get; set; }

        /// <summary>
        /// Source identifier (e.g., "SqlServer:AdventureWorks").
        /// </summary>
        public required string Source { get; set; }

        /// <summary>
        /// Database name.
        /// </summary>
        public string? Database { get; set; }

        /// <summary>
        /// Schema/namespace (e.g., "dbo").
        /// </summary>
        public string? SchemaNamespace { get; set; }

        /// <summary>
        /// Table columns.
        /// </summary>
        public required List<ColumnDefinition> Columns { get; set; }

        /// <summary>
        /// Primary key columns.
        /// </summary>
        public List<string> PrimaryKeys { get; set; } = new();

        /// <summary>
        /// Foreign key constraints.
        /// </summary>
        public List<ForeignKeyDefinition> ForeignKeys { get; set; } = new();

        /// <summary>
        /// Indexes defined on the table.
        /// </summary>
        public List<IndexDefinition> Indexes { get; set; } = new();

        /// <summary>
        /// Current version number.
        /// </summary>
        public int Version { get; set; } = 1;

        /// <summary>
        /// When this schema was first registered.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When this schema was last modified.
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Additional metadata.
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; } = new();

        /// <summary>
        /// Schema checksum for detecting changes.
        /// </summary>
        public string? Checksum { get; set; }
    }

    /// <summary>
    /// Represents a table column.
    /// </summary>
    public sealed class ColumnDefinition
    {
        /// <summary>
        /// Column name.
        /// </summary>
        public required string Name { get; set; }

        /// <summary>
        /// Data type (e.g., "int", "varchar", "decimal").
        /// </summary>
        public required string DataType { get; set; }

        /// <summary>
        /// Normalized type for cross-database compatibility.
        /// </summary>
        public string? NormalizedType { get; set; }

        /// <summary>
        /// Whether column is nullable.
        /// </summary>
        public bool IsNullable { get; set; }

        /// <summary>
        /// Whether column is part of primary key.
        /// </summary>
        public bool IsPrimaryKey { get; set; }

        /// <summary>
        /// Whether column is auto-increment.
        /// </summary>
        public bool IsAutoIncrement { get; set; }

        /// <summary>
        /// Default value expression.
        /// </summary>
        public string? DefaultValue { get; set; }

        /// <summary>
        /// Maximum length (for string/binary types).
        /// </summary>
        public int? MaxLength { get; set; }

        /// <summary>
        /// Precision (for numeric types).
        /// </summary>
        public int? Precision { get; set; }

        /// <summary>
        /// Scale (for numeric types).
        /// </summary>
        public int? Scale { get; set; }

        /// <summary>
        /// Column ordinal position.
        /// </summary>
        public int Ordinal { get; set; }

        /// <summary>
        /// Column description/comment.
        /// </summary>
        public string? Description { get; set; }
    }

    /// <summary>
    /// Represents a foreign key constraint.
    /// </summary>
    public sealed class ForeignKeyDefinition
    {
        /// <summary>
        /// Constraint name.
        /// </summary>
        public required string Name { get; set; }

        /// <summary>
        /// Source columns.
        /// </summary>
        public required List<string> Columns { get; set; }

        /// <summary>
        /// Referenced table.
        /// </summary>
        public required string ReferencedTable { get; set; }

        /// <summary>
        /// Referenced columns.
        /// </summary>
        public required List<string> ReferencedColumns { get; set; }

        /// <summary>
        /// ON DELETE action.
        /// </summary>
        public string? OnDelete { get; set; }

        /// <summary>
        /// ON UPDATE action.
        /// </summary>
        public string? OnUpdate { get; set; }
    }

    /// <summary>
    /// Represents a table index.
    /// </summary>
    public sealed class IndexDefinition
    {
        /// <summary>
        /// Index name.
        /// </summary>
        public required string Name { get; set; }

        /// <summary>
        /// Indexed columns.
        /// </summary>
        public required List<string> Columns { get; set; }

        /// <summary>
        /// Whether index is unique.
        /// </summary>
        public bool IsUnique { get; set; }

        /// <summary>
        /// Whether index is clustered.
        /// </summary>
        public bool IsClustered { get; set; }

        /// <summary>
        /// Index type (e.g., "BTREE", "HASH").
        /// </summary>
        public string? IndexType { get; set; }
    }

    /// <summary>
    /// Represents a schema version in the history.
    /// </summary>
    public sealed class SchemaVersion
    {
        /// <summary>
        /// Schema identifier.
        /// </summary>
        public required string SchemaId { get; set; }

        /// <summary>
        /// Version number.
        /// </summary>
        public int Version { get; set; }

        /// <summary>
        /// Schema definition at this version.
        /// </summary>
        public required SchemaDefinition Schema { get; set; }

        /// <summary>
        /// When this version was created.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Changes made in this version.
        /// </summary>
        public List<SchemaChange> Changes { get; set; } = new();

        /// <summary>
        /// Version description.
        /// </summary>
        public string? Description { get; set; }
    }

    /// <summary>
    /// Represents a schema change between versions.
    /// </summary>
    public sealed class SchemaChange
    {
        /// <summary>
        /// Type of change.
        /// </summary>
        public required SchemaChangeType ChangeType { get; set; }

        /// <summary>
        /// Element that changed (column name, index name, etc.).
        /// </summary>
        public required string Element { get; set; }

        /// <summary>
        /// Old value (for modifications).
        /// </summary>
        public string? OldValue { get; set; }

        /// <summary>
        /// New value (for additions/modifications).
        /// </summary>
        public string? NewValue { get; set; }

        /// <summary>
        /// Change description.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Whether this is a breaking change.
        /// </summary>
        public bool IsBreaking { get; set; }
    }

    /// <summary>
    /// Types of schema changes.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SchemaChangeType
    {
        /// <summary>Column added.</summary>
        ColumnAdded,

        /// <summary>Column removed.</summary>
        ColumnRemoved,

        /// <summary>Column modified.</summary>
        ColumnModified,

        /// <summary>Column renamed.</summary>
        ColumnRenamed,

        /// <summary>Primary key added.</summary>
        PrimaryKeyAdded,

        /// <summary>Primary key removed.</summary>
        PrimaryKeyRemoved,

        /// <summary>Foreign key added.</summary>
        ForeignKeyAdded,

        /// <summary>Foreign key removed.</summary>
        ForeignKeyRemoved,

        /// <summary>Index added.</summary>
        IndexAdded,

        /// <summary>Index removed.</summary>
        IndexRemoved,

        /// <summary>Index modified.</summary>
        IndexModified,

        /// <summary>Table renamed.</summary>
        TableRenamed,

        /// <summary>Other change.</summary>
        Other
    }

    /// <summary>
    /// Represents a comparison between two schema versions.
    /// </summary>
    public sealed class SchemaDiff
    {
        /// <summary>
        /// Schema identifier.
        /// </summary>
        public required string SchemaId { get; set; }

        /// <summary>
        /// Source version.
        /// </summary>
        public int FromVersion { get; set; }

        /// <summary>
        /// Target version.
        /// </summary>
        public int ToVersion { get; set; }

        /// <summary>
        /// List of changes.
        /// </summary>
        public List<SchemaChange> Changes { get; set; } = new();

        /// <summary>
        /// Whether there are breaking changes.
        /// </summary>
        public bool HasBreakingChanges => Changes.Any(c => c.IsBreaking);

        /// <summary>
        /// Whether schemas are compatible.
        /// </summary>
        public bool IsCompatible { get; set; } = true;

        /// <summary>
        /// Compatibility notes.
        /// </summary>
        public List<string> CompatibilityNotes { get; set; } = new();

        /// <summary>
        /// Generated migration script.
        /// </summary>
        public string? MigrationScript { get; set; }
    }

    /// <summary>
    /// Options for schema registration.
    /// </summary>
    public sealed class SchemaRegistrationOptions
    {
        /// <summary>
        /// Whether to track schema history.
        /// </summary>
        public bool TrackHistory { get; set; } = true;

        /// <summary>
        /// Whether to auto-detect changes.
        /// </summary>
        public bool AutoDetectChanges { get; set; } = true;

        /// <summary>
        /// Whether to validate schema compatibility.
        /// </summary>
        public bool ValidateCompatibility { get; set; } = true;

        /// <summary>
        /// Whether to generate migration scripts.
        /// </summary>
        public bool GenerateMigrationScripts { get; set; } = false;

        /// <summary>
        /// Custom metadata to attach.
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Schema search criteria.
    /// </summary>
    public sealed class SchemaSearchCriteria
    {
        /// <summary>
        /// Schema name pattern (supports wildcards).
        /// </summary>
        public string? NamePattern { get; set; }

        /// <summary>
        /// Source filter.
        /// </summary>
        public string? Source { get; set; }

        /// <summary>
        /// Database filter.
        /// </summary>
        public string? Database { get; set; }

        /// <summary>
        /// Column name to search for.
        /// </summary>
        public string? ColumnName { get; set; }

        /// <summary>
        /// Column type to search for.
        /// </summary>
        public string? ColumnType { get; set; }

        /// <summary>
        /// Search by metadata key-value.
        /// </summary>
        public Dictionary<string, string>? Metadata { get; set; }

        /// <summary>
        /// Maximum results to return.
        /// </summary>
        public int Limit { get; set; } = 100;
    }

    /// <summary>
    /// Schema registry statistics.
    /// </summary>
    public sealed class SchemaRegistryStats
    {
        /// <summary>
        /// Total schemas registered.
        /// </summary>
        public int TotalSchemas { get; set; }

        /// <summary>
        /// Total versions across all schemas.
        /// </summary>
        public int TotalVersions { get; set; }

        /// <summary>
        /// Schemas by source.
        /// </summary>
        public Dictionary<string, int> SchemasBySource { get; set; } = new();

        /// <summary>
        /// Most recently updated schemas.
        /// </summary>
        public List<string> RecentlyUpdated { get; set; } = new();

        /// <summary>
        /// Schemas with breaking changes in latest version.
        /// </summary>
        public List<string> SchemasWithBreakingChanges { get; set; } = new();

        /// <summary>
        /// Average schema size (columns).
        /// </summary>
        public double AverageColumnCount { get; set; }

        /// <summary>
        /// Storage size in bytes.
        /// </summary>
        public long StorageSizeBytes { get; set; }
    }
}
