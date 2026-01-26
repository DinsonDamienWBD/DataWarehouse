namespace DataWarehouse.Plugins.MySqlProtocol;

/// <summary>
/// Configuration for the MySQL wire protocol plugin.
/// Thread-safe configuration class for MySQL protocol settings.
/// </summary>
public sealed class MySqlProtocolConfig
{
    /// <summary>
    /// Port to listen on. Default is 3306 (standard MySQL port).
    /// Valid range: 1-65535.
    /// </summary>
    public int Port { get; set; } = 3306;

    /// <summary>
    /// Maximum number of concurrent client connections.
    /// Default is 151 (MySQL default).
    /// </summary>
    public int MaxConnections { get; set; } = 151;

    /// <summary>
    /// Authentication method: "mysql_native_password", "caching_sha2_password", "trust".
    /// Default is "mysql_native_password" for maximum compatibility.
    /// </summary>
    public string AuthMethod { get; set; } = "mysql_native_password";

    /// <summary>
    /// SSL mode: "disabled", "preferred", "required".
    /// Default is "preferred" for optional SSL support.
    /// </summary>
    public string SslMode { get; set; } = "preferred";

    /// <summary>
    /// Connection timeout in seconds.
    /// Default is 28800 (8 hours, MySQL default).
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 28800;

    /// <summary>
    /// Query timeout in seconds.
    /// Default is 0 (no timeout, MySQL default).
    /// </summary>
    public int QueryTimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// Server version string reported to clients.
    /// Default mimics MySQL 8.0.
    /// </summary>
    public string ServerVersion { get; set; } = "8.0.35-DataWarehouse";

    /// <summary>
    /// Default database name when not specified by client.
    /// </summary>
    public string DefaultDatabase { get; set; } = "datawarehouse";

    /// <summary>
    /// Path to SSL certificate file (PEM format).
    /// Required when SslMode is "required".
    /// </summary>
    public string? SslCertificatePath { get; set; }

    /// <summary>
    /// Path to SSL private key file (PEM format).
    /// Required when SslMode is "required".
    /// </summary>
    public string? SslKeyPath { get; set; }

    /// <summary>
    /// Maximum allowed packet size in bytes.
    /// Default is 64MB (MySQL default).
    /// </summary>
    public int MaxPacketSize { get; set; } = 67108864;
}

/// <summary>
/// MySQL protocol command types (COM_* commands).
/// These are the primary command bytes sent by clients.
/// </summary>
public enum MySqlCommand : byte
{
    /// <summary>Sleep command (deprecated).</summary>
    COM_SLEEP = 0x00,
    /// <summary>Quit/close connection.</summary>
    COM_QUIT = 0x01,
    /// <summary>Select/use database.</summary>
    COM_INIT_DB = 0x02,
    /// <summary>Execute text query.</summary>
    COM_QUERY = 0x03,
    /// <summary>Get field list (deprecated).</summary>
    COM_FIELD_LIST = 0x04,
    /// <summary>Create database (deprecated).</summary>
    COM_CREATE_DB = 0x05,
    /// <summary>Drop database (deprecated).</summary>
    COM_DROP_DB = 0x06,
    /// <summary>Refresh/reload.</summary>
    COM_REFRESH = 0x07,
    /// <summary>Shutdown server (deprecated).</summary>
    COM_SHUTDOWN = 0x08,
    /// <summary>Get server statistics.</summary>
    COM_STATISTICS = 0x09,
    /// <summary>Get process list.</summary>
    COM_PROCESS_INFO = 0x0A,
    /// <summary>Connect (internal).</summary>
    COM_CONNECT = 0x0B,
    /// <summary>Kill connection.</summary>
    COM_PROCESS_KILL = 0x0C,
    /// <summary>Dump debug info.</summary>
    COM_DEBUG = 0x0D,
    /// <summary>Ping server.</summary>
    COM_PING = 0x0E,
    /// <summary>Time (internal).</summary>
    COM_TIME = 0x0F,
    /// <summary>Delayed insert (internal).</summary>
    COM_DELAYED_INSERT = 0x10,
    /// <summary>Change user.</summary>
    COM_CHANGE_USER = 0x11,
    /// <summary>Binlog dump.</summary>
    COM_BINLOG_DUMP = 0x12,
    /// <summary>Table dump.</summary>
    COM_TABLE_DUMP = 0x13,
    /// <summary>Connect out (internal).</summary>
    COM_CONNECT_OUT = 0x14,
    /// <summary>Register slave.</summary>
    COM_REGISTER_SLAVE = 0x15,
    /// <summary>Prepare statement.</summary>
    COM_STMT_PREPARE = 0x16,
    /// <summary>Execute prepared statement.</summary>
    COM_STMT_EXECUTE = 0x17,
    /// <summary>Send long data for prepared statement.</summary>
    COM_STMT_SEND_LONG_DATA = 0x18,
    /// <summary>Close prepared statement.</summary>
    COM_STMT_CLOSE = 0x19,
    /// <summary>Reset prepared statement.</summary>
    COM_STMT_RESET = 0x1A,
    /// <summary>Set option.</summary>
    COM_SET_OPTION = 0x1B,
    /// <summary>Fetch rows from prepared statement.</summary>
    COM_STMT_FETCH = 0x1C,
    /// <summary>Daemon (internal).</summary>
    COM_DAEMON = 0x1D,
    /// <summary>Binlog dump with GTID.</summary>
    COM_BINLOG_DUMP_GTID = 0x1E,
    /// <summary>Reset connection state.</summary>
    COM_RESET_CONNECTION = 0x1F
}

/// <summary>
/// MySQL server status flags.
/// Indicates current server state to clients.
/// </summary>
[Flags]
public enum MySqlServerStatus : ushort
{
    /// <summary>No status flags set.</summary>
    None = 0,
    /// <summary>Transaction is in progress.</summary>
    SERVER_STATUS_IN_TRANS = 0x0001,
    /// <summary>Auto-commit is enabled.</summary>
    SERVER_STATUS_AUTOCOMMIT = 0x0002,
    /// <summary>More results are available.</summary>
    SERVER_MORE_RESULTS_EXISTS = 0x0008,
    /// <summary>No good index was used.</summary>
    SERVER_STATUS_NO_GOOD_INDEX_USED = 0x0010,
    /// <summary>No index was used.</summary>
    SERVER_STATUS_NO_INDEX_USED = 0x0020,
    /// <summary>Cursor exists.</summary>
    SERVER_STATUS_CURSOR_EXISTS = 0x0040,
    /// <summary>Last row was sent.</summary>
    SERVER_STATUS_LAST_ROW_SENT = 0x0080,
    /// <summary>Database was dropped.</summary>
    SERVER_STATUS_DB_DROPPED = 0x0100,
    /// <summary>No backslash escapes.</summary>
    SERVER_STATUS_NO_BACKSLASH_ESCAPES = 0x0200,
    /// <summary>Metadata has changed.</summary>
    SERVER_STATUS_METADATA_CHANGED = 0x0400,
    /// <summary>Query was slow.</summary>
    SERVER_QUERY_WAS_SLOW = 0x0800,
    /// <summary>PS output parameters.</summary>
    SERVER_PS_OUT_PARAMS = 0x1000,
    /// <summary>Transaction is read-only.</summary>
    SERVER_STATUS_IN_TRANS_READONLY = 0x2000,
    /// <summary>Session state has changed.</summary>
    SERVER_SESSION_STATE_CHANGED = 0x4000
}

/// <summary>
/// MySQL capability flags for protocol negotiation.
/// Defines features supported by client/server.
/// </summary>
[Flags]
public enum MySqlCapabilities : uint
{
    /// <summary>No capabilities.</summary>
    None = 0,
    /// <summary>Use improved old password authentication.</summary>
    CLIENT_LONG_PASSWORD = 0x00000001,
    /// <summary>Found rows instead of affected rows.</summary>
    CLIENT_FOUND_ROWS = 0x00000002,
    /// <summary>Get all column flags.</summary>
    CLIENT_LONG_FLAG = 0x00000004,
    /// <summary>Database can be specified on connect.</summary>
    CLIENT_CONNECT_WITH_DB = 0x00000008,
    /// <summary>Do not allow database.table.column syntax.</summary>
    CLIENT_NO_SCHEMA = 0x00000010,
    /// <summary>Compression protocol supported.</summary>
    CLIENT_COMPRESS = 0x00000020,
    /// <summary>ODBC client.</summary>
    CLIENT_ODBC = 0x00000040,
    /// <summary>Can use LOAD DATA LOCAL.</summary>
    CLIENT_LOCAL_FILES = 0x00000080,
    /// <summary>Parser can ignore spaces before '('.</summary>
    CLIENT_IGNORE_SPACE = 0x00000100,
    /// <summary>Protocol 4.1 support.</summary>
    CLIENT_PROTOCOL_41 = 0x00000200,
    /// <summary>wait_timeout vs wait_interactive_timeout.</summary>
    CLIENT_INTERACTIVE = 0x00000400,
    /// <summary>SSL support.</summary>
    CLIENT_SSL = 0x00000800,
    /// <summary>Ignore sigpipe.</summary>
    CLIENT_IGNORE_SIGPIPE = 0x00001000,
    /// <summary>Transaction support.</summary>
    CLIENT_TRANSACTIONS = 0x00002000,
    /// <summary>Reserved (old protocol).</summary>
    CLIENT_RESERVED = 0x00004000,
    /// <summary>Secure connection (4.1 authentication).</summary>
    CLIENT_SECURE_CONNECTION = 0x00008000,
    /// <summary>Multiple statements in single query.</summary>
    CLIENT_MULTI_STATEMENTS = 0x00010000,
    /// <summary>Multiple result sets.</summary>
    CLIENT_MULTI_RESULTS = 0x00020000,
    /// <summary>Multiple result sets for COM_STMT_EXECUTE.</summary>
    CLIENT_PS_MULTI_RESULTS = 0x00040000,
    /// <summary>Plugin authentication.</summary>
    CLIENT_PLUGIN_AUTH = 0x00080000,
    /// <summary>Connection attributes.</summary>
    CLIENT_CONNECT_ATTRS = 0x00100000,
    /// <summary>Length-encoded auth data.</summary>
    CLIENT_PLUGIN_AUTH_LENENC_CLIENT_DATA = 0x00200000,
    /// <summary>Can handle expired passwords.</summary>
    CLIENT_CAN_HANDLE_EXPIRED_PASSWORDS = 0x00400000,
    /// <summary>Session tracking.</summary>
    CLIENT_SESSION_TRACK = 0x00800000,
    /// <summary>Deprecate EOF.</summary>
    CLIENT_DEPRECATE_EOF = 0x01000000,
    /// <summary>Query attributes.</summary>
    CLIENT_QUERY_ATTRIBUTES = 0x08000000,
    /// <summary>All common capabilities for server.</summary>
    SERVER_DEFAULT = CLIENT_LONG_PASSWORD | CLIENT_FOUND_ROWS | CLIENT_LONG_FLAG |
                     CLIENT_CONNECT_WITH_DB | CLIENT_PROTOCOL_41 | CLIENT_TRANSACTIONS |
                     CLIENT_SECURE_CONNECTION | CLIENT_MULTI_STATEMENTS | CLIENT_MULTI_RESULTS |
                     CLIENT_PS_MULTI_RESULTS | CLIENT_PLUGIN_AUTH | CLIENT_CONNECT_ATTRS |
                     CLIENT_PLUGIN_AUTH_LENENC_CLIENT_DATA | CLIENT_SESSION_TRACK | CLIENT_DEPRECATE_EOF
}

/// <summary>
/// MySQL protocol constants.
/// </summary>
public static class MySqlProtocolConstants
{
    /// <summary>MySQL protocol version.</summary>
    public const byte ProtocolVersion = 10;

    /// <summary>Maximum packet size.</summary>
    public const int MaxPacketSize = 0xFFFFFF;

    /// <summary>OK packet header byte.</summary>
    public const byte OkPacketHeader = 0x00;

    /// <summary>EOF packet header byte.</summary>
    public const byte EofPacketHeader = 0xFE;

    /// <summary>Error packet header byte.</summary>
    public const byte ErrPacketHeader = 0xFF;

    /// <summary>Local infile packet header byte.</summary>
    public const byte LocalInfileHeader = 0xFB;

    /// <summary>Auth switch request header byte.</summary>
    public const byte AuthSwitchRequest = 0xFE;

    /// <summary>Auth more data header byte.</summary>
    public const byte AuthMoreData = 0x01;

    /// <summary>Default charset (utf8mb4_general_ci).</summary>
    public const byte DefaultCharset = 45;

    /// <summary>UTF-8 character set collation ID.</summary>
    public const byte Utf8Mb4GeneralCi = 45;

    /// <summary>Binary character set collation ID.</summary>
    public const byte BinaryCollation = 63;

    /// <summary>MySQL native password authentication plugin name.</summary>
    public const string AuthPluginMySqlNativePassword = "mysql_native_password";

    /// <summary>Caching SHA2 password authentication plugin name.</summary>
    public const string AuthPluginCachingSha2Password = "caching_sha2_password";

    /// <summary>SHA256 password authentication plugin name.</summary>
    public const string AuthPluginSha256Password = "sha256_password";
}

/// <summary>
/// MySQL field types for column metadata.
/// </summary>
public enum MySqlFieldType : byte
{
    /// <summary>DECIMAL type.</summary>
    MYSQL_TYPE_DECIMAL = 0x00,
    /// <summary>TINY (TINYINT) type.</summary>
    MYSQL_TYPE_TINY = 0x01,
    /// <summary>SHORT (SMALLINT) type.</summary>
    MYSQL_TYPE_SHORT = 0x02,
    /// <summary>LONG (INT) type.</summary>
    MYSQL_TYPE_LONG = 0x03,
    /// <summary>FLOAT type.</summary>
    MYSQL_TYPE_FLOAT = 0x04,
    /// <summary>DOUBLE type.</summary>
    MYSQL_TYPE_DOUBLE = 0x05,
    /// <summary>NULL type.</summary>
    MYSQL_TYPE_NULL = 0x06,
    /// <summary>TIMESTAMP type.</summary>
    MYSQL_TYPE_TIMESTAMP = 0x07,
    /// <summary>LONGLONG (BIGINT) type.</summary>
    MYSQL_TYPE_LONGLONG = 0x08,
    /// <summary>INT24 (MEDIUMINT) type.</summary>
    MYSQL_TYPE_INT24 = 0x09,
    /// <summary>DATE type.</summary>
    MYSQL_TYPE_DATE = 0x0A,
    /// <summary>TIME type.</summary>
    MYSQL_TYPE_TIME = 0x0B,
    /// <summary>DATETIME type.</summary>
    MYSQL_TYPE_DATETIME = 0x0C,
    /// <summary>YEAR type.</summary>
    MYSQL_TYPE_YEAR = 0x0D,
    /// <summary>NEWDATE (internal) type.</summary>
    MYSQL_TYPE_NEWDATE = 0x0E,
    /// <summary>VARCHAR type.</summary>
    MYSQL_TYPE_VARCHAR = 0x0F,
    /// <summary>BIT type.</summary>
    MYSQL_TYPE_BIT = 0x10,
    /// <summary>TIMESTAMP2 (internal) type.</summary>
    MYSQL_TYPE_TIMESTAMP2 = 0x11,
    /// <summary>DATETIME2 (internal) type.</summary>
    MYSQL_TYPE_DATETIME2 = 0x12,
    /// <summary>TIME2 (internal) type.</summary>
    MYSQL_TYPE_TIME2 = 0x13,
    /// <summary>JSON type.</summary>
    MYSQL_TYPE_JSON = 0xF5,
    /// <summary>NEWDECIMAL type.</summary>
    MYSQL_TYPE_NEWDECIMAL = 0xF6,
    /// <summary>ENUM type.</summary>
    MYSQL_TYPE_ENUM = 0xF7,
    /// <summary>SET type.</summary>
    MYSQL_TYPE_SET = 0xF8,
    /// <summary>TINY_BLOB type.</summary>
    MYSQL_TYPE_TINY_BLOB = 0xF9,
    /// <summary>MEDIUM_BLOB type.</summary>
    MYSQL_TYPE_MEDIUM_BLOB = 0xFA,
    /// <summary>LONG_BLOB type.</summary>
    MYSQL_TYPE_LONG_BLOB = 0xFB,
    /// <summary>BLOB type.</summary>
    MYSQL_TYPE_BLOB = 0xFC,
    /// <summary>VAR_STRING type.</summary>
    MYSQL_TYPE_VAR_STRING = 0xFD,
    /// <summary>STRING type.</summary>
    MYSQL_TYPE_STRING = 0xFE,
    /// <summary>GEOMETRY type.</summary>
    MYSQL_TYPE_GEOMETRY = 0xFF
}

/// <summary>
/// MySQL field flags for column metadata.
/// </summary>
[Flags]
public enum MySqlFieldFlags : ushort
{
    /// <summary>No flags set.</summary>
    None = 0,
    /// <summary>Field cannot be NULL.</summary>
    NOT_NULL_FLAG = 0x0001,
    /// <summary>Field is part of a primary key.</summary>
    PRI_KEY_FLAG = 0x0002,
    /// <summary>Field is part of a unique key.</summary>
    UNIQUE_KEY_FLAG = 0x0004,
    /// <summary>Field is part of a non-unique key.</summary>
    MULTIPLE_KEY_FLAG = 0x0008,
    /// <summary>Field is a BLOB.</summary>
    BLOB_FLAG = 0x0010,
    /// <summary>Field is unsigned.</summary>
    UNSIGNED_FLAG = 0x0020,
    /// <summary>Field is zerofill.</summary>
    ZEROFILL_FLAG = 0x0040,
    /// <summary>Field is binary.</summary>
    BINARY_FLAG = 0x0080,
    /// <summary>Field is an enum.</summary>
    ENUM_FLAG = 0x0100,
    /// <summary>Field is auto-increment.</summary>
    AUTO_INCREMENT_FLAG = 0x0200,
    /// <summary>Field is a timestamp.</summary>
    TIMESTAMP_FLAG = 0x0400,
    /// <summary>Field is a set.</summary>
    SET_FLAG = 0x0800,
    /// <summary>Field does not have a default value.</summary>
    NO_DEFAULT_VALUE_FLAG = 0x1000,
    /// <summary>Field is set to NOW on UPDATE.</summary>
    ON_UPDATE_NOW_FLAG = 0x2000,
    /// <summary>Field is a number (deprecated).</summary>
    NUM_FLAG = 0x8000
}

/// <summary>
/// MySQL column description for result set metadata.
/// </summary>
public sealed class MySqlColumnDescription
{
    /// <summary>
    /// Catalog name (usually "def").
    /// </summary>
    public required string Catalog { get; init; }

    /// <summary>
    /// Schema (database) name.
    /// </summary>
    public required string Schema { get; init; }

    /// <summary>
    /// Virtual table name (alias).
    /// </summary>
    public required string VirtualTable { get; init; }

    /// <summary>
    /// Physical table name.
    /// </summary>
    public required string PhysicalTable { get; init; }

    /// <summary>
    /// Virtual column name (alias).
    /// </summary>
    public required string VirtualName { get; init; }

    /// <summary>
    /// Physical column name.
    /// </summary>
    public required string PhysicalName { get; init; }

    /// <summary>
    /// Character set number.
    /// </summary>
    public ushort CharacterSet { get; init; } = MySqlProtocolConstants.Utf8Mb4GeneralCi;

    /// <summary>
    /// Maximum column length.
    /// </summary>
    public uint ColumnLength { get; init; }

    /// <summary>
    /// Column type.
    /// </summary>
    public MySqlFieldType ColumnType { get; init; }

    /// <summary>
    /// Column flags.
    /// </summary>
    public MySqlFieldFlags Flags { get; init; }

    /// <summary>
    /// Number of decimal places (for numeric types).
    /// </summary>
    public byte Decimals { get; init; }
}

/// <summary>
/// MySQL prepared statement metadata.
/// </summary>
public sealed class MySqlPreparedStatement
{
    /// <summary>
    /// Statement ID assigned by server.
    /// </summary>
    public required uint StatementId { get; init; }

    /// <summary>
    /// Original SQL query.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Number of parameters in the statement.
    /// </summary>
    public ushort ParameterCount { get; init; }

    /// <summary>
    /// Number of columns in the result.
    /// </summary>
    public ushort ColumnCount { get; init; }

    /// <summary>
    /// Parameter type definitions.
    /// </summary>
    public List<MySqlFieldType> ParameterTypes { get; init; } = new();

    /// <summary>
    /// Column definitions for result set.
    /// </summary>
    public List<MySqlColumnDescription> Columns { get; init; } = new();

    /// <summary>
    /// Timestamp when statement was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Active MySQL connection state.
/// Thread-safe connection state tracking.
/// </summary>
public sealed class MySqlConnectionState
{
    /// <summary>
    /// Unique connection identifier.
    /// </summary>
    public required string ConnectionId { get; init; }

    /// <summary>
    /// MySQL connection/thread ID.
    /// </summary>
    public required uint ThreadId { get; init; }

    /// <summary>
    /// Authenticated username.
    /// </summary>
    public string Username { get; set; } = "root";

    /// <summary>
    /// Current database.
    /// </summary>
    public string Database { get; set; } = "datawarehouse";

    /// <summary>
    /// Client IP address.
    /// </summary>
    public string? ClientAddress { get; set; }

    /// <summary>
    /// Connection timestamp.
    /// </summary>
    public DateTime ConnectedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Current transaction state.
    /// </summary>
    public bool InTransaction { get; set; }

    /// <summary>
    /// Auto-commit mode.
    /// </summary>
    public bool AutoCommit { get; set; } = true;

    /// <summary>
    /// Current server status flags.
    /// </summary>
    public MySqlServerStatus ServerStatus { get; set; } = MySqlServerStatus.SERVER_STATUS_AUTOCOMMIT;

    /// <summary>
    /// Negotiated client capabilities.
    /// </summary>
    public MySqlCapabilities ClientCapabilities { get; set; }

    /// <summary>
    /// Prepared statements by statement ID.
    /// </summary>
    public Dictionary<uint, MySqlPreparedStatement> PreparedStatements { get; } = new();

    /// <summary>
    /// Next statement ID for prepared statements.
    /// </summary>
    public uint NextStatementId { get; set; } = 1;

    /// <summary>
    /// Connection attributes from client.
    /// </summary>
    public Dictionary<string, string> ConnectionAttributes { get; } = new();

    /// <summary>
    /// Current packet sequence number.
    /// </summary>
    public byte SequenceId { get; set; }

    /// <summary>
    /// Whether SSL is active on this connection.
    /// </summary>
    public bool SslEnabled { get; set; }

    /// <summary>
    /// Character set for this connection.
    /// </summary>
    public byte CharacterSet { get; set; } = MySqlProtocolConstants.Utf8Mb4GeneralCi;
}

/// <summary>
/// MySQL error codes for common errors.
/// </summary>
public static class MySqlErrorCode
{
    /// <summary>Access denied for user.</summary>
    public const ushort ER_ACCESS_DENIED_ERROR = 1045;
    /// <summary>Unknown database.</summary>
    public const ushort ER_BAD_DB_ERROR = 1049;
    /// <summary>No database selected.</summary>
    public const ushort ER_NO_DB_ERROR = 1046;
    /// <summary>Table does not exist.</summary>
    public const ushort ER_NO_SUCH_TABLE = 1146;
    /// <summary>Column does not exist.</summary>
    public const ushort ER_BAD_FIELD_ERROR = 1054;
    /// <summary>Syntax error.</summary>
    public const ushort ER_PARSE_ERROR = 1064;
    /// <summary>Duplicate entry.</summary>
    public const ushort ER_DUP_ENTRY = 1062;
    /// <summary>Unknown command.</summary>
    public const ushort ER_UNKNOWN_COM_ERROR = 1047;
    /// <summary>Server shutdown in progress.</summary>
    public const ushort ER_SERVER_SHUTDOWN = 1053;
    /// <summary>Prepared statement not found.</summary>
    public const ushort ER_UNKNOWN_STMT_HANDLER = 1243;
    /// <summary>Wrong number of parameters.</summary>
    public const ushort ER_WRONG_PARAMCOUNT_TO_NATIVE_FCT = 1582;
    /// <summary>Unknown character set.</summary>
    public const ushort ER_UNKNOWN_CHARACTER_SET = 1115;
    /// <summary>Too many connections.</summary>
    public const ushort ER_CON_COUNT_ERROR = 1040;
    /// <summary>Query interrupted.</summary>
    public const ushort ER_QUERY_INTERRUPTED = 1317;
    /// <summary>Data too long.</summary>
    public const ushort ER_DATA_TOO_LONG = 1406;
    /// <summary>Internal error.</summary>
    public const ushort ER_INTERNAL_ERROR = 1815;
}

/// <summary>
/// MySQL SQL state codes (ANSI SQL).
/// </summary>
public static class MySqlSqlState
{
    /// <summary>Success.</summary>
    public const string Success = "00000";
    /// <summary>General warning.</summary>
    public const string Warning = "01000";
    /// <summary>Access denied.</summary>
    public const string AccessDenied = "28000";
    /// <summary>Syntax error.</summary>
    public const string SyntaxError = "42000";
    /// <summary>Unknown database.</summary>
    public const string UnknownDatabase = "42000";
    /// <summary>Unknown table.</summary>
    public const string UnknownTable = "42S02";
    /// <summary>Unknown column.</summary>
    public const string UnknownColumn = "42S22";
    /// <summary>Duplicate key.</summary>
    public const string DuplicateKey = "23000";
    /// <summary>Connection exception.</summary>
    public const string ConnectionException = "08000";
    /// <summary>Server error.</summary>
    public const string ServerError = "HY000";
}
