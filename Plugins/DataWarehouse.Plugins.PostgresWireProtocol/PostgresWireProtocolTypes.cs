namespace DataWarehouse.Plugins.PostgresWireProtocol;

/// <summary>
/// Configuration for the PostgreSQL wire protocol plugin.
/// </summary>
public sealed class PostgresWireProtocolConfig
{
    /// <summary>
    /// Port to listen on (default: 5432).
    /// </summary>
    public int Port { get; set; } = 5432;

    /// <summary>
    /// Maximum number of concurrent connections.
    /// </summary>
    public int MaxConnections { get; set; } = 100;

    /// <summary>
    /// Authentication method: "md5", "scram-sha-256", "trust".
    /// </summary>
    public string AuthMethod { get; set; } = "md5";

    /// <summary>
    /// SSL mode: "disable", "allow", "prefer", "require".
    /// </summary>
    public string SslMode { get; set; } = "prefer";

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Query timeout in seconds.
    /// </summary>
    public int QueryTimeoutSeconds { get; set; } = 300;
}

/// <summary>
/// PostgreSQL wire protocol message types.
/// </summary>
public enum PgMessageType : byte
{
    // Frontend messages
    Query = (byte)'Q',
    Parse = (byte)'P',
    Bind = (byte)'B',
    Execute = (byte)'E',
    Describe = (byte)'D',
    Close = (byte)'C',
    Sync = (byte)'S',
    Flush = (byte)'H',
    Terminate = (byte)'X',
    PasswordMessage = (byte)'p',

    // Backend messages
    Authentication = (byte)'R',
    BackendKeyData = (byte)'K',
    BindComplete = (byte)'2',
    CloseComplete = (byte)'3',
    CommandComplete = (byte)'C',
    DataRow = (byte)'D',
    EmptyQueryResponse = (byte)'I',
    ErrorResponse = (byte)'E',
    NoData = (byte)'n',
    NoticeResponse = (byte)'N',
    ParameterDescription = (byte)'t',
    ParameterStatus = (byte)'S',
    ParseComplete = (byte)'1',
    ReadyForQuery = (byte)'Z',
    RowDescription = (byte)'T',
    NotificationResponse = (byte)'A'
}

/// <summary>
/// PostgreSQL protocol constants.
/// </summary>
public static class PgProtocolConstants
{
    // Protocol version 3.0
    public const int ProtocolVersion = 196608; // 3.0 in wire format

    // SSL request code
    public const int SslRequestCode = 80877103;

    // Cancellation request code
    public const int CancelRequestCode = 80877102;

    // Authentication types
    public const int AuthOk = 0;
    public const int AuthKerberosV5 = 2;
    public const int AuthCleartextPassword = 3;
    public const int AuthMD5Password = 5;
    public const int AuthSCMCredential = 6;
    public const int AuthGSS = 7;
    public const int AuthGSSContinue = 8;
    public const int AuthSSPI = 9;
    public const int AuthSASL = 10;
    public const int AuthSASLContinue = 11;
    public const int AuthSASLFinal = 12;

    // Transaction status
    public const byte TransactionIdle = (byte)'I';
    public const byte TransactionInBlock = (byte)'T';
    public const byte TransactionFailed = (byte)'E';

    // Format codes
    public const short FormatText = 0;
    public const short FormatBinary = 1;

    // PostgreSQL OID type codes
    public const int OidBool = 16;
    public const int OidBytea = 17;
    public const int OidChar = 18;
    public const int OidName = 19;
    public const int OidInt8 = 20;
    public const int OidInt2 = 21;
    public const int OidInt4 = 23;
    public const int OidText = 25;
    public const int OidJson = 114;
    public const int OidXml = 142;
    public const int OidJsonb = 3802;
    public const int OidFloat4 = 700;
    public const int OidFloat8 = 701;
    public const int OidVarchar = 1043;
    public const int OidDate = 1082;
    public const int OidTime = 1083;
    public const int OidTimestamp = 1114;
    public const int OidTimestampTz = 1184;
    public const int OidInterval = 1186;
    public const int OidNumeric = 1700;
    public const int OidUuid = 2950;

    // Error severity levels
    public const string SeverityError = "ERROR";
    public const string SeverityFatal = "FATAL";
    public const string SeverityPanic = "PANIC";
    public const string SeverityWarning = "WARNING";
    public const string SeverityNotice = "NOTICE";
    public const string SeverityDebug = "DEBUG";
    public const string SeverityInfo = "INFO";
    public const string SeverityLog = "LOG";

    // SQL state codes
    public const string SqlStateSuccess = "00000";
    public const string SqlStateConnectionException = "08000";
    public const string SqlStateConnectionDoesNotExist = "08003";
    public const string SqlStateConnectionFailure = "08006";
    public const string SqlStateSyntaxError = "42601";
    public const string SqlStateUndefinedTable = "42P01";
    public const string SqlStateUndefinedColumn = "42703";
    public const string SqlStateInternalError = "XX000";
    public const string SqlStateDataException = "22000";
    public const string SqlStateInsufficientPrivilege = "42501";
    public const string SqlStateInvalidPassword = "28P01";
    public const string SqlStateQueryCanceled = "57014";
}

/// <summary>
/// Column description in row metadata.
/// </summary>
public sealed class PgColumnDescription
{
    public required string Name { get; init; }
    public int TableOid { get; init; }
    public short ColumnAttributeNumber { get; init; }
    public int TypeOid { get; init; }
    public short TypeSize { get; init; }
    public int TypeModifier { get; init; }
    public short FormatCode { get; init; }
}

/// <summary>
/// Prepared statement metadata.
/// </summary>
public sealed class PgPreparedStatement
{
    public required string Name { get; init; }
    public required string Query { get; init; }
    public List<int> ParameterTypeOids { get; init; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Portal (cursor) for prepared statement execution.
/// </summary>
public sealed class PgPortal
{
    public required string Name { get; init; }
    public required string StatementName { get; init; }
    public List<byte[]?> Parameters { get; init; } = new();
    public List<short> ParameterFormats { get; init; } = new();
    public List<short> ResultFormats { get; init; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Active connection state.
/// </summary>
public sealed class PgConnectionState
{
    public required string ConnectionId { get; init; }
    public string Username { get; set; } = "postgres";
    public string Database { get; set; } = "datawarehouse";
    public string ApplicationName { get; set; } = "";
    public DateTime ConnectedAt { get; init; } = DateTime.UtcNow;
    public bool InTransaction { get; set; }
    public bool TransactionFailed { get; set; }
    public Dictionary<string, PgPreparedStatement> PreparedStatements { get; } = new();
    public Dictionary<string, PgPortal> Portals { get; } = new();
    public int ProcessId { get; init; }
    public int SecretKey { get; init; }
    public Dictionary<string, string> Parameters { get; } = new();
}
