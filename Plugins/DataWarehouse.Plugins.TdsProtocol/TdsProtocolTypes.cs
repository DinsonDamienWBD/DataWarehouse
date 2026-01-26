using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.TdsProtocol;

/// <summary>
/// Configuration for the TDS (Tabular Data Stream) wire protocol plugin.
/// Provides settings for SQL Server Management Studio and Azure Data Studio compatibility.
/// </summary>
public sealed class TdsProtocolConfig
{
    /// <summary>
    /// Port to listen on (default: 1433, standard SQL Server port).
    /// </summary>
    public int Port { get; set; } = 1433;

    /// <summary>
    /// Maximum number of concurrent connections allowed.
    /// </summary>
    public int MaxConnections { get; set; } = 100;

    /// <summary>
    /// Authentication method: "sql", "windows", "integrated", "azure_ad".
    /// </summary>
    public string AuthMethod { get; set; } = "sql";

    /// <summary>
    /// SSL encryption mode: "off", "on", "required".
    /// </summary>
    public string EncryptionMode { get; set; } = "on";

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Query timeout in seconds (0 = no timeout).
    /// </summary>
    public int QueryTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Server name to advertise during PRELOGIN.
    /// </summary>
    public string ServerName { get; set; } = "DataWarehouse";

    /// <summary>
    /// SQL Server version to report (12.0.0 = SQL Server 2014 for TDS 7.4 compatibility).
    /// </summary>
    public string ServerVersion { get; set; } = "16.0.1000";

    /// <summary>
    /// Default database for new connections.
    /// </summary>
    public string DefaultDatabase { get; set; } = "datawarehouse";

    /// <summary>
    /// Packet size for TDS communication (default: 4096).
    /// </summary>
    public int PacketSize { get; set; } = 4096;

    /// <summary>
    /// Enable connection pooling internally.
    /// </summary>
    public bool EnableConnectionPooling { get; set; } = true;

    /// <summary>
    /// Connection pool minimum size.
    /// </summary>
    public int PoolMinSize { get; set; } = 5;

    /// <summary>
    /// Connection pool maximum size.
    /// </summary>
    public int PoolMaxSize { get; set; } = 50;

    /// <summary>
    /// Connection lifetime in the pool (seconds).
    /// </summary>
    public int PoolConnectionLifetimeSeconds { get; set; } = 3600;

    /// <summary>
    /// Path to SSL certificate file (PFX format).
    /// </summary>
    public string? SslCertificatePath { get; set; }

    /// <summary>
    /// Password for the SSL certificate.
    /// </summary>
    public string? SslCertificatePassword { get; set; }
}

/// <summary>
/// TDS protocol version identifiers.
/// </summary>
public static class TdsVersion
{
    /// <summary>TDS 7.0 (SQL Server 7.0)</summary>
    public const uint Tds70 = 0x00000070;

    /// <summary>TDS 7.1 (SQL Server 2000)</summary>
    public const uint Tds71 = 0x01000071;

    /// <summary>TDS 7.1 Revision 1 (SQL Server 2000 SP1)</summary>
    public const uint Tds71Rev1 = 0x01000071;

    /// <summary>TDS 7.2 (SQL Server 2005)</summary>
    public const uint Tds72 = 0x02000972;

    /// <summary>TDS 7.3 (SQL Server 2008)</summary>
    public const uint Tds73 = 0x03000A73;

    /// <summary>TDS 7.3 B (SQL Server 2008 R2)</summary>
    public const uint Tds73B = 0x03000B73;

    /// <summary>TDS 7.4 (SQL Server 2012+)</summary>
    public const uint Tds74 = 0x04000074;

    /// <summary>Current supported version</summary>
    public const uint Current = Tds74;
}

/// <summary>
/// TDS packet types for the header.
/// </summary>
public enum TdsPacketType : byte
{
    /// <summary>SQL batch request</summary>
    SqlBatch = 0x01,

    /// <summary>Pre-TDS7 login</summary>
    PreTds7Login = 0x02,

    /// <summary>RPC request</summary>
    Rpc = 0x03,

    /// <summary>Tabular result</summary>
    TabularResult = 0x04,

    /// <summary>Attention signal</summary>
    Attention = 0x06,

    /// <summary>Bulk load data</summary>
    BulkLoad = 0x07,

    /// <summary>Federated authentication token</summary>
    FedAuthToken = 0x08,

    /// <summary>Transaction manager request</summary>
    TransactionManager = 0x0E,

    /// <summary>TDS7+ login</summary>
    Tds7Login = 0x10,

    /// <summary>SSPI message</summary>
    Sspi = 0x11,

    /// <summary>Pre-login message</summary>
    PreLogin = 0x12
}

/// <summary>
/// TDS packet status flags.
/// </summary>
[Flags]
public enum TdsPacketStatus : byte
{
    /// <summary>Normal packet, more to follow</summary>
    Normal = 0x00,

    /// <summary>End of message (last packet)</summary>
    EndOfMessage = 0x01,

    /// <summary>Client-to-server: ignore this event</summary>
    IgnoreEvent = 0x02,

    /// <summary>Reset connection</summary>
    ResetConnection = 0x08,

    /// <summary>Reset connection but keep transaction state</summary>
    ResetConnectionSkipTran = 0x10
}

/// <summary>
/// TDS token types for server responses.
/// </summary>
public enum TdsTokenType : byte
{
    /// <summary>Column metadata</summary>
    ColMetadata = 0x81,

    /// <summary>Tabular result finished</summary>
    TabName = 0xA4,

    /// <summary>Column info</summary>
    ColInfo = 0xA5,

    /// <summary>Order by columns</summary>
    Order = 0xA9,

    /// <summary>Error message</summary>
    Error = 0xAA,

    /// <summary>Info message</summary>
    Info = 0xAB,

    /// <summary>Return status from stored procedure</summary>
    ReturnStatus = 0x79,

    /// <summary>Return value from RPC</summary>
    ReturnValue = 0xAC,

    /// <summary>Login acknowledgment</summary>
    LoginAck = 0xAD,

    /// <summary>Feature extension acknowledgment</summary>
    FeatureExtAck = 0xAE,

    /// <summary>Row data</summary>
    Row = 0xD1,

    /// <summary>NBC Row data (null bitmap compressed)</summary>
    NbcRow = 0xD2,

    /// <summary>Done (end of result set)</summary>
    Done = 0xFD,

    /// <summary>Done in procedure</summary>
    DoneProc = 0xFE,

    /// <summary>Done in SQL</summary>
    DoneInProc = 0xFF,

    /// <summary>Environment change</summary>
    EnvChange = 0xE3,

    /// <summary>Session state</summary>
    SessionState = 0xE4,

    /// <summary>SSPI token</summary>
    Sspi = 0xED
}

/// <summary>
/// PRELOGIN option tokens.
/// </summary>
public enum PreLoginOption : byte
{
    /// <summary>Protocol version</summary>
    Version = 0x00,

    /// <summary>Encryption setting</summary>
    Encryption = 0x01,

    /// <summary>Instance name</summary>
    Instance = 0x02,

    /// <summary>Thread ID</summary>
    ThreadId = 0x03,

    /// <summary>MARS enabled</summary>
    Mars = 0x04,

    /// <summary>Trace ID</summary>
    TraceId = 0x05,

    /// <summary>Federated authentication required</summary>
    FedAuthRequired = 0x06,

    /// <summary>NONCE for encryption</summary>
    Nonce = 0x07,

    /// <summary>Terminator</summary>
    Terminator = 0xFF
}

/// <summary>
/// Encryption negotiation values.
/// </summary>
public enum TdsEncryption : byte
{
    /// <summary>Encryption off</summary>
    Off = 0x00,

    /// <summary>Encryption on</summary>
    On = 0x01,

    /// <summary>Encryption not supported</summary>
    NotSupported = 0x02,

    /// <summary>Encryption required</summary>
    Required = 0x03
}

/// <summary>
/// SQL Server data types for TDS protocol.
/// </summary>
public enum TdsDataType : byte
{
    /// <summary>Null type</summary>
    Null = 0x1F,

    /// <summary>Tiny int (1 byte)</summary>
    TinyInt = 0x30,

    /// <summary>Small int (2 bytes)</summary>
    SmallInt = 0x34,

    /// <summary>Int (4 bytes)</summary>
    Int = 0x38,

    /// <summary>Big int (8 bytes)</summary>
    BigInt = 0x7F,

    /// <summary>Float (8 bytes)</summary>
    Float = 0x3E,

    /// <summary>Real (4 bytes)</summary>
    Real = 0x3B,

    /// <summary>Money (8 bytes)</summary>
    Money = 0x3C,

    /// <summary>Small money (4 bytes)</summary>
    SmallMoney = 0x7A,

    /// <summary>Bit</summary>
    Bit = 0x32,

    /// <summary>Datetime (8 bytes)</summary>
    DateTime = 0x3D,

    /// <summary>Small datetime (4 bytes)</summary>
    SmallDateTime = 0x3F,

    /// <summary>Decimal/Numeric with precision</summary>
    Decimal = 0x6A,

    /// <summary>Numeric with precision</summary>
    Numeric = 0x6C,

    /// <summary>Unique identifier (GUID)</summary>
    UniqueIdentifier = 0x24,

    /// <summary>Variable-length binary</summary>
    VarBinary = 0xA5,

    /// <summary>Variable-length character</summary>
    VarChar = 0xA7,

    /// <summary>Fixed-length binary</summary>
    Binary = 0xAD,

    /// <summary>Fixed-length character</summary>
    Char = 0xAF,

    /// <summary>Unicode variable-length character</summary>
    NVarChar = 0xE7,

    /// <summary>Unicode fixed-length character</summary>
    NChar = 0xEF,

    /// <summary>Text (deprecated)</summary>
    Text = 0x23,

    /// <summary>NText (deprecated)</summary>
    NText = 0x63,

    /// <summary>Image (deprecated)</summary>
    Image = 0x22,

    /// <summary>Timestamp/rowversion</summary>
    Timestamp = 0xBB,

    /// <summary>SQL Variant</summary>
    Variant = 0x62,

    /// <summary>XML</summary>
    Xml = 0xF1,

    /// <summary>User-defined type</summary>
    Udt = 0xF0,

    /// <summary>Date (TDS 7.3+)</summary>
    Date = 0x28,

    /// <summary>Time (TDS 7.3+)</summary>
    Time = 0x29,

    /// <summary>DateTime2 (TDS 7.3+)</summary>
    DateTime2 = 0x2A,

    /// <summary>DateTimeOffset (TDS 7.3+)</summary>
    DateTimeOffset = 0x2B,

    /// <summary>Int with nullable</summary>
    IntN = 0x26,

    /// <summary>Float with nullable</summary>
    FloatN = 0x6D,

    /// <summary>Money with nullable</summary>
    MoneyN = 0x6E,

    /// <summary>DateTime with nullable</summary>
    DateTimeN = 0x6F,

    /// <summary>Bit with nullable</summary>
    BitN = 0x68,

    /// <summary>Guid with nullable</summary>
    GuidN = 0x24,

    /// <summary>Decimal with nullable</summary>
    DecimalN = 0x6A,

    /// <summary>Numeric with nullable</summary>
    NumericN = 0x6C
}

/// <summary>
/// Environment change types.
/// </summary>
public enum EnvChangeType : byte
{
    /// <summary>Database changed</summary>
    Database = 0x01,

    /// <summary>Language changed</summary>
    Language = 0x02,

    /// <summary>Character set changed</summary>
    CharacterSet = 0x03,

    /// <summary>Packet size changed</summary>
    PacketSize = 0x04,

    /// <summary>Unicode sorting locale ID</summary>
    SqlCollation = 0x07,

    /// <summary>Begin transaction</summary>
    BeginTransaction = 0x08,

    /// <summary>Commit transaction</summary>
    CommitTransaction = 0x09,

    /// <summary>Rollback transaction</summary>
    RollbackTransaction = 0x0A,

    /// <summary>Enlist DTC transaction</summary>
    EnlistDtcTransaction = 0x0B,

    /// <summary>Defect transaction</summary>
    DefectTransaction = 0x0C,

    /// <summary>Real time log shipping</summary>
    RealTimeLogShipping = 0x0D,

    /// <summary>Promote transaction</summary>
    PromoteTransaction = 0x0F,

    /// <summary>Transaction manager address</summary>
    TransactionManagerAddress = 0x10,

    /// <summary>Transaction ended</summary>
    TransactionEnded = 0x11,

    /// <summary>Reset connection completion acknowledgment</summary>
    ResetConnectionAck = 0x12,

    /// <summary>User instance started</summary>
    UserInstance = 0x13,

    /// <summary>Routing information</summary>
    Routing = 0x14
}

/// <summary>
/// DONE token status flags.
/// </summary>
[Flags]
public enum DoneStatus : ushort
{
    /// <summary>Final result set</summary>
    Final = 0x0000,

    /// <summary>More results to follow</summary>
    More = 0x0001,

    /// <summary>Error occurred</summary>
    Error = 0x0002,

    /// <summary>Transaction in progress</summary>
    InTransaction = 0x0004,

    /// <summary>Row count valid</summary>
    Count = 0x0010,

    /// <summary>Attention acknowledged</summary>
    Attention = 0x0020,

    /// <summary>Server error</summary>
    ServerError = 0x0100
}

/// <summary>
/// Column metadata for result sets.
/// </summary>
public sealed class TdsColumnMetadata
{
    /// <summary>Column name.</summary>
    public required string Name { get; init; }

    /// <summary>TDS data type.</summary>
    public TdsDataType DataType { get; init; }

    /// <summary>Maximum length for variable-length types.</summary>
    public int MaxLength { get; init; }

    /// <summary>Precision for decimal/numeric types.</summary>
    public byte Precision { get; init; }

    /// <summary>Scale for decimal/numeric types.</summary>
    public byte Scale { get; init; }

    /// <summary>Collation for string types.</summary>
    public uint Collation { get; init; }

    /// <summary>Column flags (nullable, identity, etc.).</summary>
    public ushort Flags { get; init; }

    /// <summary>Whether the column is nullable.</summary>
    public bool IsNullable => (Flags & 0x0001) != 0;

    /// <summary>Whether the column is an identity column.</summary>
    public bool IsIdentity => (Flags & 0x0010) != 0;

    /// <summary>Whether the column is computed.</summary>
    public bool IsComputed => (Flags & 0x0020) != 0;

    /// <summary>Table name for the column (optional).</summary>
    public string? TableName { get; init; }

    /// <summary>Schema name (optional).</summary>
    public string? SchemaName { get; init; }
}

/// <summary>
/// TDS connection state for a session.
/// </summary>
public sealed class TdsConnectionState
{
    /// <summary>Unique connection identifier.</summary>
    public required string ConnectionId { get; init; }

    /// <summary>Authenticated user name.</summary>
    public string Username { get; set; } = "sa";

    /// <summary>Current database context.</summary>
    public string Database { get; set; } = "datawarehouse";

    /// <summary>Client application name.</summary>
    public string ApplicationName { get; set; } = "";

    /// <summary>Client hostname.</summary>
    public string Hostname { get; set; } = "";

    /// <summary>Negotiated TDS protocol version.</summary>
    public uint TdsVersion { get; set; } = TdsProtocol.TdsVersion.Tds74;

    /// <summary>Negotiated packet size.</summary>
    public int PacketSize { get; set; } = 4096;

    /// <summary>Whether encryption is enabled.</summary>
    public bool EncryptionEnabled { get; set; }

    /// <summary>Whether MARS is enabled.</summary>
    public bool MarsEnabled { get; set; }

    /// <summary>Connection timestamp.</summary>
    public DateTime ConnectedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Whether in an active transaction.</summary>
    public bool InTransaction { get; set; }

    /// <summary>Current transaction ID.</summary>
    public long TransactionId { get; set; }

    /// <summary>Process ID assigned to this connection.</summary>
    public int ProcessId { get; init; }

    /// <summary>Language setting.</summary>
    public string Language { get; set; } = "us_english";

    /// <summary>Date format setting.</summary>
    public string DateFormat { get; set; } = "mdy";

    /// <summary>Client interface name.</summary>
    public string ClientInterfaceName { get; set; } = "";

    /// <summary>Client PID.</summary>
    public int ClientPid { get; set; }

    /// <summary>Additional session options.</summary>
    public Dictionary<string, string> Options { get; } = new();

    /// <summary>Prepared statements for this session.</summary>
    public ConcurrentDictionary<int, TdsPreparedStatement> PreparedStatements { get; } = new();

    /// <summary>Next prepared statement handle.</summary>
    private int _nextPrepareHandle;

    /// <summary>Gets the next prepared statement handle.</summary>
    public int GetNextPrepareHandle() => Interlocked.Increment(ref _nextPrepareHandle);
}

/// <summary>
/// Prepared statement metadata.
/// </summary>
public sealed class TdsPreparedStatement
{
    /// <summary>Prepared statement handle.</summary>
    public int Handle { get; init; }

    /// <summary>Original SQL query.</summary>
    public required string Query { get; init; }

    /// <summary>Parameter definitions.</summary>
    public List<TdsParameterDefinition> Parameters { get; init; } = new();

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Last execution timestamp.</summary>
    public DateTime? LastExecutedAt { get; set; }

    /// <summary>Execution count.</summary>
    public long ExecutionCount { get; set; }
}

/// <summary>
/// Parameter definition for prepared statements and RPC calls.
/// </summary>
public sealed class TdsParameterDefinition
{
    /// <summary>Parameter name (with @ prefix).</summary>
    public required string Name { get; init; }

    /// <summary>TDS data type.</summary>
    public TdsDataType DataType { get; init; }

    /// <summary>Maximum length for variable types.</summary>
    public int MaxLength { get; init; }

    /// <summary>Precision for decimal/numeric.</summary>
    public byte Precision { get; init; }

    /// <summary>Scale for decimal/numeric.</summary>
    public byte Scale { get; init; }

    /// <summary>Whether the parameter is output.</summary>
    public bool IsOutput { get; init; }

    /// <summary>Parameter value (for bound parameters).</summary>
    public object? Value { get; set; }
}

/// <summary>
/// Query execution result for TDS protocol.
/// </summary>
public sealed class TdsQueryResult
{
    /// <summary>Whether the query returned an empty result.</summary>
    public bool IsEmpty { get; init; }

    /// <summary>Column metadata for result set.</summary>
    public List<TdsColumnMetadata> Columns { get; init; } = new();

    /// <summary>Result rows (each row is a list of column values).</summary>
    public List<List<object?>> Rows { get; init; } = new();

    /// <summary>Number of rows affected (for DML statements).</summary>
    public long RowsAffected { get; init; }

    /// <summary>Command tag (SELECT, INSERT, UPDATE, DELETE, etc.).</summary>
    public string CommandTag { get; init; } = "";

    /// <summary>Error message if query failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>SQL Server error number.</summary>
    public int ErrorNumber { get; init; }

    /// <summary>Error severity level.</summary>
    public byte ErrorSeverity { get; init; }

    /// <summary>Error state.</summary>
    public byte ErrorState { get; init; }

    /// <summary>Info/warning messages.</summary>
    public List<TdsMessage> Messages { get; init; } = new();

    /// <summary>Return value from stored procedure.</summary>
    public int? ReturnValue { get; init; }

    /// <summary>Output parameter values.</summary>
    public Dictionary<string, object?> OutputParameters { get; init; } = new();
}

/// <summary>
/// Info or warning message.
/// </summary>
public sealed class TdsMessage
{
    /// <summary>Message number.</summary>
    public int Number { get; init; }

    /// <summary>Severity level (0-25).</summary>
    public byte Severity { get; init; }

    /// <summary>State code.</summary>
    public byte State { get; init; }

    /// <summary>Message text.</summary>
    public required string Text { get; init; }

    /// <summary>Server name.</summary>
    public string ServerName { get; init; } = "";

    /// <summary>Procedure name (if applicable).</summary>
    public string ProcedureName { get; init; } = "";

    /// <summary>Line number in batch.</summary>
    public int LineNumber { get; init; }
}

/// <summary>
/// PRELOGIN response data.
/// </summary>
public sealed class PreLoginResponse
{
    /// <summary>Server version.</summary>
    public Version ServerVersion { get; init; } = new(16, 0, 0, 0);

    /// <summary>Encryption setting.</summary>
    public TdsEncryption Encryption { get; init; } = TdsEncryption.On;

    /// <summary>Instance name.</summary>
    public string InstanceName { get; init; } = "";

    /// <summary>Thread ID.</summary>
    public uint ThreadId { get; init; }

    /// <summary>MARS enabled.</summary>
    public bool MarsEnabled { get; init; }

    /// <summary>Trace ID.</summary>
    public Guid TraceId { get; init; }

    /// <summary>Federated auth required.</summary>
    public bool FedAuthRequired { get; init; }

    /// <summary>Encryption nonce.</summary>
    public byte[]? Nonce { get; init; }
}

/// <summary>
/// LOGIN7 message data parsed from client.
/// </summary>
public sealed class Login7Data
{
    /// <summary>TDS version requested by client.</summary>
    public uint TdsVersion { get; init; }

    /// <summary>Requested packet size.</summary>
    public int PacketSize { get; init; }

    /// <summary>Client program version.</summary>
    public uint ClientProgVer { get; init; }

    /// <summary>Client process ID.</summary>
    public uint ClientPid { get; init; }

    /// <summary>Connection ID.</summary>
    public uint ConnectionId { get; init; }

    /// <summary>Option flags 1.</summary>
    public byte OptionFlags1 { get; init; }

    /// <summary>Option flags 2.</summary>
    public byte OptionFlags2 { get; init; }

    /// <summary>Type flags.</summary>
    public byte TypeFlags { get; init; }

    /// <summary>Option flags 3.</summary>
    public byte OptionFlags3 { get; init; }

    /// <summary>Client time zone.</summary>
    public int ClientTimeZone { get; init; }

    /// <summary>Client LCID.</summary>
    public uint ClientLcid { get; init; }

    /// <summary>Client hostname.</summary>
    public string Hostname { get; init; } = "";

    /// <summary>Username for SQL authentication.</summary>
    public string Username { get; init; } = "";

    /// <summary>Password for SQL authentication.</summary>
    public string Password { get; init; } = "";

    /// <summary>Application name.</summary>
    public string ApplicationName { get; init; } = "";

    /// <summary>Server name.</summary>
    public string ServerName { get; init; } = "";

    /// <summary>Client library name.</summary>
    public string LibraryName { get; init; } = "";

    /// <summary>Language preference.</summary>
    public string Language { get; init; } = "";

    /// <summary>Initial database.</summary>
    public string Database { get; init; } = "";

    /// <summary>Client MAC address.</summary>
    public byte[] ClientMac { get; init; } = new byte[6];

    /// <summary>Attach database file path.</summary>
    public string AttachDbFile { get; init; } = "";

    /// <summary>New password (for password change).</summary>
    public string NewPassword { get; init; } = "";

    /// <summary>SSPI data for Windows authentication.</summary>
    public byte[]? SspiData { get; init; }

    /// <summary>Feature extension data.</summary>
    public byte[]? FeatureExtData { get; init; }
}

/// <summary>
/// Connection pool entry.
/// </summary>
public sealed class PooledConnection
{
    /// <summary>Connection state.</summary>
    public required TdsConnectionState State { get; init; }

    /// <summary>Whether the connection is currently in use.</summary>
    public bool InUse { get; set; }

    /// <summary>When the connection was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>When the connection was last used.</summary>
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Number of times this connection has been reused.</summary>
    public int ReuseCount { get; set; }
}

/// <summary>
/// SQL Server error and message numbers.
/// </summary>
public static class TdsErrorNumbers
{
    /// <summary>Login failed</summary>
    public const int LoginFailed = 18456;

    /// <summary>Cannot open database</summary>
    public const int CannotOpenDatabase = 4060;

    /// <summary>Invalid object name</summary>
    public const int InvalidObjectName = 208;

    /// <summary>Invalid column name</summary>
    public const int InvalidColumnName = 207;

    /// <summary>Syntax error</summary>
    public const int SyntaxError = 102;

    /// <summary>General error</summary>
    public const int GeneralError = 50000;

    /// <summary>Timeout expired</summary>
    public const int TimeoutExpired = 258;

    /// <summary>Transaction state error</summary>
    public const int TransactionError = 266;

    /// <summary>Permission denied</summary>
    public const int PermissionDenied = 229;

    /// <summary>Database does not exist</summary>
    public const int DatabaseNotExist = 911;

    /// <summary>Object already exists</summary>
    public const int ObjectAlreadyExists = 2714;

    /// <summary>Constraint violation</summary>
    public const int ConstraintViolation = 547;

    /// <summary>Primary key violation</summary>
    public const int PrimaryKeyViolation = 2627;

    /// <summary>Unique constraint violation</summary>
    public const int UniqueConstraintViolation = 2601;

    /// <summary>Deadlock victim</summary>
    public const int DeadlockVictim = 1205;

    /// <summary>Connection reset</summary>
    public const int ConnectionReset = 18002;
}

/// <summary>
/// SQL Server severity levels.
/// </summary>
public static class TdsSeverity
{
    /// <summary>Informational message (0-10)</summary>
    public const byte Informational = 10;

    /// <summary>Warning (11-16)</summary>
    public const byte Warning = 11;

    /// <summary>User error (11-16)</summary>
    public const byte UserError = 16;

    /// <summary>Fatal error (17-19)</summary>
    public const byte FatalError = 17;

    /// <summary>System error (20-24)</summary>
    public const byte SystemError = 20;

    /// <summary>Fatal system error (25)</summary>
    public const byte FatalSystemError = 25;
}

/// <summary>
/// Default SQL Server collation (Latin1_General_CI_AS).
/// </summary>
public static class TdsCollation
{
    /// <summary>Default collation value for Latin1_General_CI_AS</summary>
    public const uint Default = 0x00000409;

    /// <summary>Collation flags for case-insensitive, accent-sensitive</summary>
    public const uint CiAs = 0x00000000;
}
