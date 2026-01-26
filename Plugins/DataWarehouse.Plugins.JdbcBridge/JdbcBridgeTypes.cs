namespace DataWarehouse.Plugins.JdbcBridge;

/// <summary>
/// Configuration for the JDBC Bridge server.
/// </summary>
public sealed class JdbcBridgeConfig
{
    /// <summary>
    /// Port to listen on (default: 9527 - JDBC bridge default).
    /// </summary>
    public int Port { get; set; } = 9527;

    /// <summary>
    /// Maximum number of concurrent connections.
    /// </summary>
    public int MaxConnections { get; set; } = 100;

    /// <summary>
    /// Authentication enabled (default: true).
    /// </summary>
    public bool AuthenticationEnabled { get; set; } = true;

    /// <summary>
    /// Default database name.
    /// </summary>
    public string DefaultDatabase { get; set; } = "datawarehouse";

    /// <summary>
    /// Default schema name.
    /// </summary>
    public string DefaultSchema { get; set; } = "public";

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Query timeout in seconds (default: 5 minutes).
    /// </summary>
    public int QueryTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum batch size for batch operations.
    /// </summary>
    public int MaxBatchSize { get; set; } = 1000;

    /// <summary>
    /// Enable SSL/TLS encryption.
    /// </summary>
    public bool SslEnabled { get; set; }

    /// <summary>
    /// Server version reported to clients.
    /// </summary>
    public string ServerVersion { get; set; } = "1.0.0";
}

/// <summary>
/// JDBC wire protocol message types (custom binary protocol).
/// </summary>
public enum JdbcMessageType : byte
{
    // Connection messages
    Connect = 0x01,
    ConnectResponse = 0x02,
    Disconnect = 0x03,
    Ping = 0x04,
    Pong = 0x05,

    // Statement messages
    CreateStatement = 0x10,
    PrepareStatement = 0x11,
    CloseStatement = 0x12,

    // Execution messages
    ExecuteQuery = 0x20,
    ExecuteUpdate = 0x21,
    ExecuteBatch = 0x22,

    // Result messages
    ResultSetMetadata = 0x30,
    ResultSetRow = 0x31,
    ResultSetComplete = 0x32,
    UpdateCount = 0x33,

    // Transaction messages
    SetAutoCommit = 0x40,
    Commit = 0x41,
    Rollback = 0x42,
    SetSavepoint = 0x43,
    ReleaseSavepoint = 0x44,
    RollbackToSavepoint = 0x45,

    // Metadata messages
    GetDatabaseMetadata = 0x50,
    GetTables = 0x51,
    GetColumns = 0x52,
    GetPrimaryKeys = 0x53,
    GetIndexInfo = 0x54,
    GetTypeInfo = 0x55,
    GetCatalogs = 0x56,
    GetSchemas = 0x57,
    GetTableTypes = 0x58,
    GetProcedures = 0x59,

    // Error messages
    Error = 0xE0,
    Warning = 0xE1,
    SQLException = 0xE2
}

/// <summary>
/// JDBC SQL types (maps to java.sql.Types).
/// </summary>
public static class JdbcSqlTypes
{
    public const int BIT = -7;
    public const int TINYINT = -6;
    public const int SMALLINT = 5;
    public const int INTEGER = 4;
    public const int BIGINT = -5;
    public const int FLOAT = 6;
    public const int REAL = 7;
    public const int DOUBLE = 8;
    public const int NUMERIC = 2;
    public const int DECIMAL = 3;
    public const int CHAR = 1;
    public const int VARCHAR = 12;
    public const int LONGVARCHAR = -1;
    public const int DATE = 91;
    public const int TIME = 92;
    public const int TIMESTAMP = 93;
    public const int BINARY = -2;
    public const int VARBINARY = -3;
    public const int LONGVARBINARY = -4;
    public const int NULL = 0;
    public const int OTHER = 1111;
    public const int JAVA_OBJECT = 2000;
    public const int DISTINCT = 2001;
    public const int STRUCT = 2002;
    public const int ARRAY = 2003;
    public const int BLOB = 2004;
    public const int CLOB = 2005;
    public const int REF = 2006;
    public const int DATALINK = 70;
    public const int BOOLEAN = 16;
    public const int ROWID = -8;
    public const int NCHAR = -15;
    public const int NVARCHAR = -9;
    public const int LONGNVARCHAR = -16;
    public const int NCLOB = 2011;
    public const int SQLXML = 2009;
    public const int REF_CURSOR = 2012;
    public const int TIME_WITH_TIMEZONE = 2013;
    public const int TIMESTAMP_WITH_TIMEZONE = 2014;
}

/// <summary>
/// SQL state codes for SQLException.
/// </summary>
public static class JdbcSqlState
{
    public const string Success = "00000";
    public const string Warning = "01000";
    public const string NoData = "02000";
    public const string ConnectionException = "08000";
    public const string ConnectionDoesNotExist = "08003";
    public const string ConnectionFailure = "08006";
    public const string SqlClientUnableToEstablishConnection = "08001";
    public const string TransactionResolutionUnknown = "08007";
    public const string FeatureNotSupported = "0A000";
    public const string InvalidTargetTypeSpecification = "0D000";
    public const string InvalidSchemaName = "3F000";
    public const string SyntaxErrorOrAccessRuleViolation = "42000";
    public const string SyntaxError = "42601";
    public const string InvalidCatalogName = "3D000";
    public const string InvalidParameterValue = "22023";
    public const string DataException = "22000";
    public const string NumericValueOutOfRange = "22003";
    public const string InvalidDatetimeFormat = "22007";
    public const string DivisionByZero = "22012";
    public const string NullValueNotAllowed = "22004";
    public const string StringDataRightTruncation = "22001";
    public const string InvalidTransactionState = "25000";
    public const string ActiveSqlTransaction = "25001";
    public const string NoActiveSqlTransaction = "25P01";
    public const string InsufficientPrivilege = "42501";
    public const string InternalError = "XX000";
}

/// <summary>
/// JDBC ResultSet concurrency modes.
/// </summary>
public enum ResultSetConcurrency
{
    ReadOnly = 1007,
    Updatable = 1008
}

/// <summary>
/// JDBC ResultSet type modes.
/// </summary>
public enum ResultSetType
{
    ForwardOnly = 1003,
    ScrollInsensitive = 1004,
    ScrollSensitive = 1005
}

/// <summary>
/// JDBC ResultSet holdability modes.
/// </summary>
public enum ResultSetHoldability
{
    HoldCursorsOverCommit = 1,
    CloseCursorsAtCommit = 2
}

/// <summary>
/// JDBC transaction isolation levels.
/// </summary>
public enum TransactionIsolation
{
    None = 0,
    ReadUncommitted = 1,
    ReadCommitted = 2,
    RepeatableRead = 4,
    Serializable = 8
}

/// <summary>
/// Column metadata for ResultSetMetaData.
/// </summary>
public sealed class JdbcColumnMetadata
{
    /// <summary>
    /// Column label (alias or name).
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Column name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Schema name.
    /// </summary>
    public string SchemaName { get; init; } = "";

    /// <summary>
    /// Table name.
    /// </summary>
    public string TableName { get; init; } = "";

    /// <summary>
    /// Catalog name.
    /// </summary>
    public string CatalogName { get; init; } = "";

    /// <summary>
    /// SQL type from java.sql.Types.
    /// </summary>
    public int SqlType { get; init; }

    /// <summary>
    /// Type name.
    /// </summary>
    public string TypeName { get; init; } = "";

    /// <summary>
    /// Column precision.
    /// </summary>
    public int Precision { get; init; }

    /// <summary>
    /// Column scale.
    /// </summary>
    public int Scale { get; init; }

    /// <summary>
    /// Display size.
    /// </summary>
    public int DisplaySize { get; init; }

    /// <summary>
    /// Whether the column is nullable.
    /// </summary>
    public int Nullable { get; init; } = 1; // columnNullable

    /// <summary>
    /// Whether the column is auto-increment.
    /// </summary>
    public bool AutoIncrement { get; init; }

    /// <summary>
    /// Whether the column is case-sensitive.
    /// </summary>
    public bool CaseSensitive { get; init; } = true;

    /// <summary>
    /// Whether the column is searchable.
    /// </summary>
    public bool Searchable { get; init; } = true;

    /// <summary>
    /// Whether the column is a currency value.
    /// </summary>
    public bool Currency { get; init; }

    /// <summary>
    /// Whether the column is signed.
    /// </summary>
    public bool Signed { get; init; }

    /// <summary>
    /// Whether the column is read-only.
    /// </summary>
    public bool ReadOnly { get; init; } = true;

    /// <summary>
    /// Whether the column is writable.
    /// </summary>
    public bool Writable { get; init; }

    /// <summary>
    /// Whether the column is definitely writable.
    /// </summary>
    public bool DefinitelyWritable { get; init; }

    /// <summary>
    /// Column class name.
    /// </summary>
    public string ClassName { get; init; } = "java.lang.Object";
}

/// <summary>
/// Prepared statement parameter metadata.
/// </summary>
public sealed class JdbcParameterMetadata
{
    /// <summary>
    /// Parameter index (1-based).
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// SQL type from java.sql.Types.
    /// </summary>
    public int SqlType { get; init; }

    /// <summary>
    /// Type name.
    /// </summary>
    public string TypeName { get; init; } = "";

    /// <summary>
    /// Parameter precision.
    /// </summary>
    public int Precision { get; init; }

    /// <summary>
    /// Parameter scale.
    /// </summary>
    public int Scale { get; init; }

    /// <summary>
    /// Parameter mode (IN, OUT, INOUT).
    /// </summary>
    public int Mode { get; init; } = 1; // parameterModeIn

    /// <summary>
    /// Whether the parameter is nullable.
    /// </summary>
    public int Nullable { get; init; } = 1; // parameterNullable
}

/// <summary>
/// Prepared statement metadata.
/// </summary>
public sealed class JdbcPreparedStatementInfo
{
    /// <summary>
    /// Statement handle ID.
    /// </summary>
    public required int StatementId { get; init; }

    /// <summary>
    /// The SQL query.
    /// </summary>
    public required string Sql { get; init; }

    /// <summary>
    /// Parameter metadata.
    /// </summary>
    public List<JdbcParameterMetadata> Parameters { get; init; } = new();

    /// <summary>
    /// Result set column metadata (if known at prepare time).
    /// </summary>
    public List<JdbcColumnMetadata>? Columns { get; init; }

    /// <summary>
    /// When the statement was prepared.
    /// </summary>
    public DateTime PreparedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Savepoint information.
/// </summary>
public sealed class JdbcSavepoint
{
    /// <summary>
    /// Savepoint ID.
    /// </summary>
    public required int Id { get; init; }

    /// <summary>
    /// Savepoint name (null for unnamed).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// When the savepoint was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// JDBC connection state.
/// </summary>
public sealed class JdbcConnectionState
{
    /// <summary>
    /// Connection ID.
    /// </summary>
    public required string ConnectionId { get; init; }

    /// <summary>
    /// User name.
    /// </summary>
    public string User { get; set; } = "anonymous";

    /// <summary>
    /// Catalog (database) name.
    /// </summary>
    public string Catalog { get; set; } = "datawarehouse";

    /// <summary>
    /// Schema name.
    /// </summary>
    public string Schema { get; set; } = "public";

    /// <summary>
    /// Client application name.
    /// </summary>
    public string ApplicationName { get; set; } = "";

    /// <summary>
    /// Connection timestamp.
    /// </summary>
    public DateTime ConnectedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Auto-commit mode.
    /// </summary>
    public bool AutoCommit { get; set; } = true;

    /// <summary>
    /// Read-only mode.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Transaction isolation level.
    /// </summary>
    public TransactionIsolation Isolation { get; set; } = TransactionIsolation.ReadCommitted;

    /// <summary>
    /// Whether in an active transaction.
    /// </summary>
    public bool InTransaction { get; set; }

    /// <summary>
    /// Prepared statements by ID.
    /// </summary>
    public Dictionary<int, JdbcPreparedStatementInfo> PreparedStatements { get; } = new();

    /// <summary>
    /// Active savepoints.
    /// </summary>
    public Dictionary<int, JdbcSavepoint> Savepoints { get; } = new();

    /// <summary>
    /// Next statement ID.
    /// </summary>
    private int _nextStatementId;

    /// <summary>
    /// Next savepoint ID.
    /// </summary>
    private int _nextSavepointId;

    /// <summary>
    /// Gets the next statement ID.
    /// </summary>
    public int GetNextStatementId() => Interlocked.Increment(ref _nextStatementId);

    /// <summary>
    /// Gets the next savepoint ID.
    /// </summary>
    public int GetNextSavepointId() => Interlocked.Increment(ref _nextSavepointId);

    /// <summary>
    /// Connection properties.
    /// </summary>
    public Dictionary<string, string> Properties { get; } = new();
}

/// <summary>
/// Query execution result.
/// </summary>
public sealed class JdbcQueryResult
{
    /// <summary>
    /// Column metadata.
    /// </summary>
    public List<JdbcColumnMetadata> Columns { get; init; } = new();

    /// <summary>
    /// Row data (each row is a list of values).
    /// </summary>
    public List<object?[]> Rows { get; init; } = new();

    /// <summary>
    /// Update count (-1 for SELECT, >= 0 for DML).
    /// </summary>
    public int UpdateCount { get; init; } = -1;

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// SQL state for error.
    /// </summary>
    public string? SqlState { get; init; }

    /// <summary>
    /// Warning messages.
    /// </summary>
    public List<string> Warnings { get; init; } = new();
}
