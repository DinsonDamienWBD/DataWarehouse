using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Connectors
{
    #region Enumerations

    /// <summary>
    /// Connector categories for different data source types.
    /// </summary>
    public enum ConnectorCategory
    {
        /// <summary>
        /// Database connectors (PostgreSQL, MySQL, MongoDB, Cassandra, Redis).
        /// </summary>
        Database,

        /// <summary>
        /// Cloud storage connectors (S3, Azure Blob, GCS, Backblaze B2).
        /// </summary>
        CloudStorage,

        /// <summary>
        /// SaaS platform connectors (Salesforce, HubSpot, Zendesk, Jira).
        /// </summary>
        SaaS,

        /// <summary>
        /// Messaging system connectors (Kafka, RabbitMQ, Pulsar, NATS).
        /// </summary>
        Messaging,

        /// <summary>
        /// Analytics platform connectors (Snowflake, Databricks, BigQuery).
        /// </summary>
        Analytics,

        /// <summary>
        /// Enterprise system connectors (SAP, Oracle EBS, Microsoft Dynamics).
        /// </summary>
        Enterprise,

        /// <summary>
        /// Legacy system connectors (Mainframe, AS/400, Tape libraries).
        /// </summary>
        Legacy
    }

    /// <summary>
    /// Connection state of a data connector.
    /// </summary>
    public enum ConnectionState
    {
        /// <summary>
        /// Connector is disconnected.
        /// </summary>
        Disconnected,

        /// <summary>
        /// Connector is currently establishing a connection.
        /// </summary>
        Connecting,

        /// <summary>
        /// Connector is successfully connected.
        /// </summary>
        Connected,

        /// <summary>
        /// Connector is in an error state.
        /// </summary>
        Error,

        /// <summary>
        /// Connector is attempting to reconnect.
        /// </summary>
        Reconnecting
    }

    /// <summary>
    /// Write mode for data operations.
    /// </summary>
    public enum WriteMode
    {
        /// <summary>
        /// Insert new records only.
        /// </summary>
        Insert,

        /// <summary>
        /// Insert or update records (upsert).
        /// </summary>
        Upsert,

        /// <summary>
        /// Update existing records only.
        /// </summary>
        Update,

        /// <summary>
        /// Delete records.
        /// </summary>
        Delete
    }

    /// <summary>
    /// Type of change in Change Data Capture.
    /// </summary>
    public enum ChangeType
    {
        /// <summary>
        /// Record was inserted.
        /// </summary>
        Insert,

        /// <summary>
        /// Record was updated.
        /// </summary>
        Update,

        /// <summary>
        /// Record was deleted.
        /// </summary>
        Delete
    }

    /// <summary>
    /// Connector capabilities flags.
    /// </summary>
    [Flags]
    public enum ConnectorCapabilities
    {
        /// <summary>
        /// No capabilities.
        /// </summary>
        None = 0,

        /// <summary>
        /// Supports reading data.
        /// </summary>
        Read = 1,

        /// <summary>
        /// Supports writing data.
        /// </summary>
        Write = 2,

        /// <summary>
        /// Supports schema discovery.
        /// </summary>
        Schema = 4,

        /// <summary>
        /// Supports transactional operations.
        /// </summary>
        Transactions = 8,

        /// <summary>
        /// Supports streaming operations.
        /// </summary>
        Streaming = 16,

        /// <summary>
        /// Supports change tracking/CDC.
        /// </summary>
        ChangeTracking = 32,

        /// <summary>
        /// Supports bulk operations.
        /// </summary>
        BulkOperations = 64
    }

    #endregion

    #region Configuration and Results

    /// <summary>
    /// Configuration for establishing a data connector connection.
    /// </summary>
    public record ConnectorConfig(
        /// <summary>
        /// Connection string for the data source.
        /// </summary>
        string ConnectionString,

        /// <summary>
        /// Additional connection properties.
        /// </summary>
        Dictionary<string, string> Properties,

        /// <summary>
        /// Optional connection timeout.
        /// </summary>
        TimeSpan? Timeout = null,

        /// <summary>
        /// Optional maximum retry attempts.
        /// </summary>
        int? MaxRetries = null
    );

    /// <summary>
    /// Result of a connection attempt.
    /// </summary>
    public record ConnectionResult(
        /// <summary>
        /// Whether the connection was successful.
        /// </summary>
        bool Success,

        /// <summary>
        /// Error message if connection failed.
        /// </summary>
        string? ErrorMessage,

        /// <summary>
        /// Server information if connection succeeded.
        /// </summary>
        Dictionary<string, object>? ServerInfo
    );

    /// <summary>
    /// Result of a write operation.
    /// </summary>
    public record WriteResult(
        /// <summary>
        /// Number of records successfully written.
        /// </summary>
        long RecordsWritten,

        /// <summary>
        /// Number of records that failed to write.
        /// </summary>
        long RecordsFailed,

        /// <summary>
        /// Error messages for failed records.
        /// </summary>
        string[]? Errors
    );

    #endregion

    #region Schema Types

    /// <summary>
    /// Schema definition for a data source.
    /// </summary>
    public record DataSchema(
        /// <summary>
        /// Name of the schema (table, collection, topic, etc.).
        /// </summary>
        string Name,

        /// <summary>
        /// Fields in the schema.
        /// </summary>
        DataSchemaField[] Fields,

        /// <summary>
        /// Primary key field names.
        /// </summary>
        string[] PrimaryKeys,

        /// <summary>
        /// Additional schema metadata.
        /// </summary>
        Dictionary<string, object>? Metadata
    );

    /// <summary>
    /// Field definition in a data schema.
    /// </summary>
    public record DataSchemaField(
        /// <summary>
        /// Name of the field.
        /// </summary>
        string Name,

        /// <summary>
        /// Data type of the field.
        /// </summary>
        string DataType,

        /// <summary>
        /// Whether the field is nullable.
        /// </summary>
        bool Nullable,

        /// <summary>
        /// Maximum length for string/binary fields.
        /// </summary>
        int? MaxLength,

        /// <summary>
        /// Additional field properties.
        /// </summary>
        Dictionary<string, object>? Properties
    );

    #endregion

    #region Data Types

    /// <summary>
    /// A single data record.
    /// </summary>
    public record DataRecord(
        /// <summary>
        /// Field values for the record.
        /// </summary>
        Dictionary<string, object?> Values,

        /// <summary>
        /// Position/offset of the record in the source.
        /// </summary>
        long? Position,

        /// <summary>
        /// Timestamp of the record.
        /// </summary>
        DateTimeOffset? Timestamp
    );

    /// <summary>
    /// Query parameters for reading data.
    /// </summary>
    public record DataQuery(
        /// <summary>
        /// Target table or collection name.
        /// </summary>
        string? TableOrCollection = null,

        /// <summary>
        /// Filter expression (SQL WHERE, NoSQL filter, etc.).
        /// </summary>
        string? Filter = null,

        /// <summary>
        /// Fields to retrieve (null for all fields).
        /// </summary>
        string[]? Fields = null,

        /// <summary>
        /// Maximum number of records to return.
        /// </summary>
        int? Limit = null,

        /// <summary>
        /// Number of records to skip.
        /// </summary>
        long? Offset = null,

        /// <summary>
        /// Order by expression.
        /// </summary>
        string? OrderBy = null
    );

    /// <summary>
    /// Options for write operations.
    /// </summary>
    public record WriteOptions(
        /// <summary>
        /// Write mode (Insert, Upsert, Update, Delete).
        /// </summary>
        WriteMode Mode = WriteMode.Insert,

        /// <summary>
        /// Target table or collection name.
        /// </summary>
        string? TargetTable = null,

        /// <summary>
        /// Create target if it doesn't exist.
        /// </summary>
        bool CreateIfNotExists = false,

        /// <summary>
        /// Batch size for bulk operations.
        /// </summary>
        int BatchSize = 1000
    );

    #endregion

    #region Change Data Capture

    /// <summary>
    /// Options for Change Data Capture.
    /// </summary>
    public record CdcOptions(
        /// <summary>
        /// Include before-image values for updates/deletes.
        /// </summary>
        bool IncludeBeforeImages = false,

        /// <summary>
        /// Starting position for reading changes (LSN, timestamp, etc.).
        /// </summary>
        string? StartFromPosition = null
    );

    /// <summary>
    /// A change event from Change Data Capture.
    /// </summary>
    public record ChangeEvent(
        /// <summary>
        /// Type of change (Insert, Update, Delete).
        /// </summary>
        ChangeType Type,

        /// <summary>
        /// Table or collection that changed.
        /// </summary>
        string Table,

        /// <summary>
        /// Old values before the change (for updates/deletes).
        /// </summary>
        DataRecord? OldValues,

        /// <summary>
        /// New values after the change.
        /// </summary>
        DataRecord NewValues,

        /// <summary>
        /// Position of this change in the change stream.
        /// </summary>
        string Position
    );

    #endregion

    #region Core Interfaces

    /// <summary>
    /// Core interface for universal data connectors.
    /// Provides abstraction for connecting to and interacting with diverse data sources.
    /// </summary>
    public interface IDataConnector
    {
        /// <summary>
        /// Unique identifier for this connector instance.
        /// </summary>
        string ConnectorId { get; }

        /// <summary>
        /// Human-readable name of the connector.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Category of the data source.
        /// </summary>
        ConnectorCategory ConnectorCategory { get; }

        /// <summary>
        /// Current connection state.
        /// </summary>
        ConnectionState State { get; }

        /// <summary>
        /// Capabilities supported by this connector.
        /// </summary>
        ConnectorCapabilities Capabilities { get; }

        /// <summary>
        /// Connects to the data source.
        /// </summary>
        /// <param name="config">Connection configuration.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result indicating success or failure.</returns>
        Task<ConnectionResult> ConnectAsync(ConnectorConfig config, CancellationToken ct = default);

        /// <summary>
        /// Disconnects from the data source.
        /// </summary>
        /// <returns>Task representing the disconnect operation.</returns>
        Task DisconnectAsync();

        /// <summary>
        /// Tests connectivity to the data source.
        /// </summary>
        /// <returns>True if connection is active and responsive.</returns>
        Task<bool> TestConnectionAsync();

        /// <summary>
        /// Retrieves the schema/structure of the data source.
        /// </summary>
        /// <returns>Schema information.</returns>
        Task<DataSchema> GetSchemaAsync();

        /// <summary>
        /// Reads data from the source.
        /// </summary>
        /// <param name="query">Query parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Async enumerable of data records.</returns>
        IAsyncEnumerable<DataRecord> ReadAsync(DataQuery query, CancellationToken ct = default);

        /// <summary>
        /// Writes data to the source (if supported).
        /// </summary>
        /// <param name="records">Records to write.</param>
        /// <param name="options">Write options.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result of the write operation.</returns>
        Task<WriteResult> WriteAsync(IAsyncEnumerable<DataRecord> records, WriteOptions options, CancellationToken ct = default);
    }

    /// <summary>
    /// Interface for Change Data Capture support.
    /// Enables tracking and capturing changes to data over time.
    /// </summary>
    public interface IChangeDataCapture
    {
        /// <summary>
        /// Starts capturing changes for specified tables/collections.
        /// </summary>
        /// <param name="tables">Tables/collections to monitor.</param>
        /// <param name="options">CDC options.</param>
        /// <returns>Capture ID for this CDC session.</returns>
        Task<string> StartCapturingAsync(string[] tables, CdcOptions options);

        /// <summary>
        /// Gets changes from an active capture session.
        /// </summary>
        /// <param name="captureId">Capture session ID.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Async enumerable of change events.</returns>
        IAsyncEnumerable<ChangeEvent> GetChangesAsync(string captureId, CancellationToken ct = default);

        /// <summary>
        /// Stops an active capture session.
        /// </summary>
        /// <param name="captureId">Capture session ID to stop.</param>
        /// <returns>Task representing the stop operation.</returns>
        Task StopCapturingAsync(string captureId);
    }

    #endregion
}
