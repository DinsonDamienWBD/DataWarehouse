using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DataWarehouse.Plugins.OracleTnsProtocol;

/// <summary>
/// Configuration for the Oracle TNS wire protocol plugin.
/// Provides all settings needed for Oracle-compatible network communication.
/// </summary>
/// <remarks>
/// Thread-safety: This class is designed for single-threaded initialization,
/// then read-only access during operation.
/// </remarks>
public sealed class OracleTnsProtocolConfig
{
    /// <summary>
    /// Port to listen on (default: 1521 - standard Oracle listener port).
    /// </summary>
    /// <value>Valid range: 1-65535. Default: 1521.</value>
    public int Port { get; set; } = 1521;

    /// <summary>
    /// Maximum number of concurrent connections.
    /// </summary>
    /// <value>Valid range: 1-10000. Default: 100.</value>
    public int MaxConnections { get; set; } = 100;

    /// <summary>
    /// Authentication methods: "o3logon", "o5logon", "o7logon", "o8logon".
    /// O7LOGON provides password-based authentication with DES obfuscation.
    /// O8LOGON uses SHA-1 hashing for improved security.
    /// </summary>
    /// <value>Default: "o8logon" for optimal security.</value>
    public string AuthMethod { get; set; } = "o8logon";

    /// <summary>
    /// Native Network Encryption mode: "disabled", "accepted", "requested", "required".
    /// </summary>
    /// <value>Default: "accepted" to allow but not require encryption.</value>
    public string EncryptionMode { get; set; } = "accepted";

    /// <summary>
    /// Supported encryption algorithms: "AES256", "AES192", "AES128", "3DES168", "3DES112", "RC4_256", "RC4_128".
    /// </summary>
    /// <value>Default: AES256 for highest security.</value>
    public string[] EncryptionAlgorithms { get; set; } = new[] { "AES256", "AES192", "AES128" };

    /// <summary>
    /// Data integrity algorithms: "SHA256", "SHA1".
    /// </summary>
    /// <value>Default: SHA256 for highest security. MD5 removed due to collision vulnerabilities.</value>
    public string[] IntegrityAlgorithms { get; set; } = new[] { "SHA256", "SHA1" };

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    /// <value>Valid range: 1-3600. Default: 60.</value>
    public int ConnectionTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Query timeout in seconds (0 = no timeout).
    /// </summary>
    /// <value>Valid range: 0-86400. Default: 300.</value>
    public int QueryTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Service name for TNS connection (default Oracle service).
    /// </summary>
    /// <value>Default: "DATAWAREHOUSE".</value>
    public string ServiceName { get; set; } = "DATAWAREHOUSE";

    /// <summary>
    /// System Identifier (SID) for legacy connections.
    /// </summary>
    /// <value>Default: "DWSID".</value>
    public string Sid { get; set; } = "DWSID";

    /// <summary>
    /// Maximum Session Data Unit size in bytes.
    /// </summary>
    /// <value>Valid range: 512-65535. Default: 8192.</value>
    public int SduSize { get; set; } = 8192;

    /// <summary>
    /// Maximum Transport Data Unit size in bytes.
    /// </summary>
    /// <value>Valid range: 512-65535. Default: 65535.</value>
    public int TduSize { get; set; } = 65535;

    /// <summary>
    /// Enable Oracle version banner spoofing for client compatibility.
    /// </summary>
    /// <value>Default: true to maximize client compatibility.</value>
    public bool EnableVersionSpoofing { get; set; } = true;

    /// <summary>
    /// Oracle version to report to clients.
    /// </summary>
    /// <value>Default: "19.0.0.0.0" (Oracle 19c).</value>
    public string ReportedVersion { get; set; } = "19.0.0.0.0";
}

/// <summary>
/// Oracle TNS packet types as defined in the Oracle Net Services protocol specification.
/// </summary>
/// <remarks>
/// TNS (Transparent Network Substrate) packets use a single byte to identify
/// the packet type in the header.
/// </remarks>
public enum TnsPacketType : byte
{
    /// <summary>
    /// Connection request packet (CONNECT).
    /// Sent by client to initiate connection with connection string.
    /// </summary>
    Connect = 1,

    /// <summary>
    /// Connection accepted packet (ACCEPT).
    /// Sent by server to confirm connection establishment.
    /// </summary>
    Accept = 2,

    /// <summary>
    /// Acknowledge packet (ACK).
    /// Confirms receipt of certain packets.
    /// </summary>
    Ack = 3,

    /// <summary>
    /// Connection refused packet (REFUSE).
    /// Sent by server when connection cannot be established.
    /// </summary>
    Refuse = 4,

    /// <summary>
    /// Connection redirect packet (REDIRECT).
    /// Instructs client to connect to a different address.
    /// </summary>
    Redirect = 5,

    /// <summary>
    /// Data transfer packet (DATA).
    /// Contains SQL commands, results, and other protocol data.
    /// </summary>
    Data = 6,

    /// <summary>
    /// NULL packet for keep-alive.
    /// </summary>
    Null = 7,

    /// <summary>
    /// Abort connection packet.
    /// </summary>
    Abort = 9,

    /// <summary>
    /// Resend request packet.
    /// </summary>
    Resend = 11,

    /// <summary>
    /// Marker packet for out-of-band signaling.
    /// </summary>
    Marker = 12,

    /// <summary>
    /// Attention packet for interrupt signaling.
    /// </summary>
    Attention = 13,

    /// <summary>
    /// Control packet for protocol control.
    /// </summary>
    Control = 14,

    /// <summary>
    /// Highest packet type value.
    /// </summary>
    MaxPacketType = 19
}

/// <summary>
/// Oracle TNS data packet function codes for the Two-Task Interface (TTI) protocol.
/// </summary>
/// <remarks>
/// These function codes appear within DATA packets to specify the operation type.
/// The Two-Task Interface is Oracle's client-server communication protocol.
/// </remarks>
public enum TtiFunctionCode : byte
{
    /// <summary>
    /// Protocol negotiation request.
    /// </summary>
    ProtocolNegotiation = 0x01,

    /// <summary>
    /// Data type negotiation.
    /// </summary>
    DataTypeNegotiation = 0x02,

    /// <summary>
    /// Handshake/version exchange.
    /// </summary>
    Handshake = 0x03,

    /// <summary>
    /// Authentication request (O3LOGON, O5LOGON, O7LOGON, O8LOGON).
    /// </summary>
    Authentication = 0x08,

    /// <summary>
    /// Set protocol options.
    /// </summary>
    SetProtocol = 0x09,

    /// <summary>
    /// Open cursor (OOpen).
    /// </summary>
    OpenCursor = 0x0E,

    /// <summary>
    /// Close cursor (OClose).
    /// </summary>
    CloseCursor = 0x0F,

    /// <summary>
    /// Parse SQL statement (OAll7).
    /// </summary>
    ParseStatement = 0x03,

    /// <summary>
    /// Execute SQL statement.
    /// </summary>
    Execute = 0x04,

    /// <summary>
    /// Fetch rows from cursor (OFetch).
    /// </summary>
    Fetch = 0x05,

    /// <summary>
    /// Commit transaction (OCommit).
    /// </summary>
    Commit = 0x0B,

    /// <summary>
    /// Rollback transaction (ORollback).
    /// </summary>
    Rollback = 0x0C,

    /// <summary>
    /// Cancel current operation (OCancel).
    /// </summary>
    Cancel = 0x39,

    /// <summary>
    /// Describe column metadata.
    /// </summary>
    Describe = 0x44,

    /// <summary>
    /// Logout/disconnect (OLogoff).
    /// </summary>
    Logout = 0x09,

    /// <summary>
    /// Native Network Encryption negotiation.
    /// </summary>
    EncryptionNegotiation = 0xDE,

    /// <summary>
    /// Session control operation.
    /// </summary>
    SessionControl = 0x07,

    /// <summary>
    /// LOB operations.
    /// </summary>
    LobOperation = 0x60,

    /// <summary>
    /// PL/SQL execution.
    /// </summary>
    PlSqlExecute = 0x47,

    /// <summary>
    /// Version exchange.
    /// </summary>
    VersionExchange = 0x01,

    /// <summary>
    /// O3LOGON authentication (legacy).
    /// </summary>
    O3Logon = 0x73,

    /// <summary>
    /// O5LOGON authentication (challenge-response).
    /// </summary>
    O5Logon = 0x76,

    /// <summary>
    /// O7LOGON authentication (DES-based).
    /// </summary>
    O7Logon = 0x77,

    /// <summary>
    /// O8LOGON authentication (SHA-1 based).
    /// </summary>
    O8Logon = 0x78
}

/// <summary>
/// Oracle SQL types as OIDs for column metadata.
/// </summary>
/// <remarks>
/// These values correspond to Oracle's internal type identifiers
/// used in describe operations and data transfer.
/// </remarks>
public static class OracleTypeOid
{
    /// <summary>VARCHAR2 string type (OID: 1).</summary>
    public const int Varchar2 = 1;

    /// <summary>NUMBER numeric type (OID: 2).</summary>
    public const int Number = 2;

    /// <summary>INTEGER alias for NUMBER (OID: 3).</summary>
    public const int Integer = 3;

    /// <summary>FLOAT alias for NUMBER (OID: 4).</summary>
    public const int Float = 4;

    /// <summary>LONG text type (OID: 8).</summary>
    public const int Long = 8;

    /// <summary>DATE date/time type (OID: 12).</summary>
    public const int Date = 12;

    /// <summary>RAW binary type (OID: 23).</summary>
    public const int Raw = 23;

    /// <summary>LONG RAW binary type (OID: 24).</summary>
    public const int LongRaw = 24;

    /// <summary>CHAR fixed-length string (OID: 96).</summary>
    public const int Char = 96;

    /// <summary>BINARY_FLOAT IEEE float (OID: 100).</summary>
    public const int BinaryFloat = 100;

    /// <summary>BINARY_DOUBLE IEEE double (OID: 101).</summary>
    public const int BinaryDouble = 101;

    /// <summary>CLOB character LOB (OID: 112).</summary>
    public const int Clob = 112;

    /// <summary>BLOB binary LOB (OID: 113).</summary>
    public const int Blob = 113;

    /// <summary>BFILE external file locator (OID: 114).</summary>
    public const int Bfile = 114;

    /// <summary>TIMESTAMP with fractional seconds (OID: 180).</summary>
    public const int Timestamp = 180;

    /// <summary>TIMESTAMP WITH TIME ZONE (OID: 181).</summary>
    public const int TimestampTz = 181;

    /// <summary>INTERVAL YEAR TO MONTH (OID: 182).</summary>
    public const int IntervalYm = 182;

    /// <summary>INTERVAL DAY TO SECOND (OID: 183).</summary>
    public const int IntervalDs = 183;

    /// <summary>TIMESTAMP WITH LOCAL TIME ZONE (OID: 231).</summary>
    public const int TimestampLtz = 231;

    /// <summary>ROWID row identifier (OID: 104).</summary>
    public const int Rowid = 104;

    /// <summary>NVARCHAR2 national character varying (OID: 1).</summary>
    public const int Nvarchar2 = 1;

    /// <summary>NCHAR national character fixed (OID: 96).</summary>
    public const int Nchar = 96;

    /// <summary>NCLOB national character LOB (OID: 112).</summary>
    public const int Nclob = 112;

    /// <summary>BOOLEAN type (OID: 252, Oracle 23c+).</summary>
    public const int Boolean = 252;

    /// <summary>JSON type (OID: 119, Oracle 21c+).</summary>
    public const int Json = 119;

    /// <summary>XMLType (OID: 108).</summary>
    public const int XmlType = 108;
}

/// <summary>
/// Oracle protocol constants for TNS and TTC communication.
/// </summary>
/// <remarks>
/// These constants define various protocol parameters, error codes,
/// and standard values used throughout Oracle Net Services communication.
/// </remarks>
public static class OracleProtocolConstants
{
    /// <summary>TNS protocol version for Oracle 19c.</summary>
    public const int TnsVersion = 319;

    /// <summary>Minimum supported TNS version.</summary>
    public const int TnsVersionMin = 300;

    /// <summary>Default Session Data Unit size.</summary>
    public const int DefaultSduSize = 8192;

    /// <summary>Minimum SDU size.</summary>
    public const int MinSduSize = 512;

    /// <summary>Maximum SDU size.</summary>
    public const int MaxSduSize = 65535;

    /// <summary>Default Transport Data Unit size.</summary>
    public const int DefaultTduSize = 65535;

    /// <summary>TNS header length in bytes.</summary>
    public const int TnsHeaderLength = 8;

    /// <summary>Authentication OK response code.</summary>
    public const int AuthOk = 0;

    /// <summary>Invalid username/password error code.</summary>
    public const int AuthInvalidCredentials = 1017;

    /// <summary>Account locked error code.</summary>
    public const int AuthAccountLocked = 28000;

    /// <summary>Password expired error code.</summary>
    public const int AuthPasswordExpired = 28001;

    /// <summary>Session killed error code.</summary>
    public const int SessionKilled = 28;

    /// <summary>End of data marker.</summary>
    public const int EndOfData = 0x00;

    /// <summary>More data flag.</summary>
    public const int MoreData = 0x01;

    /// <summary>Error marker in response.</summary>
    public const int ErrorMarker = 0x04;

    /// <summary>Success return code.</summary>
    public const int ReturnCodeSuccess = 0;

    /// <summary>No data found return code.</summary>
    public const int ReturnCodeNoDataFound = 100;

    /// <summary>Fetch completed flag.</summary>
    public const int FetchCompleted = 0x1000;

    /// <summary>Native Network Encryption AES-256 algorithm ID.</summary>
    public const int NneAes256 = 1;

    /// <summary>Native Network Encryption AES-192 algorithm ID.</summary>
    public const int NneAes192 = 2;

    /// <summary>Native Network Encryption AES-128 algorithm ID.</summary>
    public const int NneAes128 = 3;

    /// <summary>Native Network Encryption 3DES-168 algorithm ID.</summary>
    public const int Nne3Des168 = 4;

    /// <summary>Native Network Encryption SHA-256 integrity algorithm ID.</summary>
    public const int NneSha256 = 1;

    /// <summary>Native Network Encryption SHA-1 integrity algorithm ID.</summary>
    public const int NneSha1 = 2;

    /// <summary>Native Network Encryption MD5 integrity algorithm ID.</summary>
    public const int NneMd5 = 3;

    /// <summary>Maximum rows in a single fetch operation.</summary>
    public const int MaxFetchRows = 1000;

    /// <summary>Default array fetch size.</summary>
    public const int DefaultFetchSize = 100;

    // Error message constants
    /// <summary>Generic internal error message.</summary>
    public const string ErrorInternal = "ORA-00600: internal error code";

    /// <summary>Invalid username/password error message.</summary>
    public const string ErrorInvalidCredentials = "ORA-01017: invalid username/password; logon denied";

    /// <summary>Not connected error message.</summary>
    public const string ErrorNotConnected = "ORA-03114: not connected to ORACLE";

    /// <summary>Table not found error message.</summary>
    public const string ErrorTableNotFound = "ORA-00942: table or view does not exist";

    /// <summary>Column not found error message.</summary>
    public const string ErrorColumnNotFound = "ORA-00904: invalid identifier";

    /// <summary>Syntax error message.</summary>
    public const string ErrorSyntax = "ORA-00900: invalid SQL statement";

    /// <summary>Invalid cursor error message.</summary>
    public const string ErrorInvalidCursor = "ORA-01001: invalid cursor";

    /// <summary>Fetch out of sequence error message.</summary>
    public const string ErrorFetchOutOfSequence = "ORA-01002: fetch out of sequence";

    /// <summary>Insufficient privileges error message.</summary>
    public const string ErrorInsufficientPrivileges = "ORA-01031: insufficient privileges";
}

/// <summary>
/// Represents column metadata in Oracle result sets.
/// </summary>
/// <remarks>
/// This class mirrors the column descriptor structure sent
/// in Oracle describe responses.
/// </remarks>
public sealed class OracleColumnDescription
{
    /// <summary>
    /// Gets the column name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the Oracle type OID for the column.
    /// </summary>
    /// <seealso cref="OracleTypeOid"/>
    public int TypeOid { get; init; }

    /// <summary>
    /// Gets the maximum display size in characters.
    /// </summary>
    public int DisplaySize { get; init; }

    /// <summary>
    /// Gets the precision for numeric types.
    /// </summary>
    /// <value>0 for non-numeric types.</value>
    public int Precision { get; init; }

    /// <summary>
    /// Gets the scale for numeric types.
    /// </summary>
    /// <value>0 for non-numeric types.</value>
    public int Scale { get; init; }

    /// <summary>
    /// Gets whether the column allows NULL values.
    /// </summary>
    public bool IsNullable { get; init; } = true;

    /// <summary>
    /// Gets the character set ID (for character types).
    /// </summary>
    public int CharsetId { get; init; } = 873; // AL32UTF8

    /// <summary>
    /// Gets the national character set form (for NCHAR types).
    /// </summary>
    public int CharsetForm { get; init; } = 1;

    /// <summary>
    /// Gets the column position (1-based).
    /// </summary>
    public int Position { get; init; }
}

/// <summary>
/// Represents an Oracle cursor for statement execution.
/// </summary>
/// <remarks>
/// Cursors manage the lifecycle of SQL statement execution,
/// including parsing, binding, execution, and fetching.
/// Thread-safety: Cursors are single-threaded per connection.
/// </remarks>
public sealed class OracleCursor
{
    /// <summary>
    /// Gets the cursor identifier.
    /// </summary>
    public required int CursorId { get; init; }

    /// <summary>
    /// Gets or sets the SQL statement associated with this cursor.
    /// </summary>
    public string SqlStatement { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the statement has been parsed.
    /// </summary>
    public bool IsParsed { get; set; }

    /// <summary>
    /// Gets or sets whether the statement has been executed.
    /// </summary>
    public bool IsExecuted { get; set; }

    /// <summary>
    /// Gets the column descriptions for the result set.
    /// </summary>
    public List<OracleColumnDescription> Columns { get; } = new();

    /// <summary>
    /// Gets the current fetch position in the result set.
    /// </summary>
    public int FetchPosition { get; set; }

    /// <summary>
    /// Gets or sets the cached rows from execution.
    /// </summary>
    public List<List<byte[]?>> CachedRows { get; set; } = new();

    /// <summary>
    /// Gets or sets the number of rows affected by DML operations.
    /// </summary>
    public long RowsAffected { get; set; }

    /// <summary>
    /// Gets the bind parameters for prepared execution.
    /// </summary>
    public List<OracleBindParameter> BindParameters { get; } = new();

    /// <summary>
    /// Gets the timestamp when this cursor was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the statement type (SELECT, INSERT, UPDATE, DELETE, etc.).
    /// </summary>
    public OracleStatementType StatementType { get; set; } = OracleStatementType.Unknown;
}

/// <summary>
/// Oracle statement types for cursor operations.
/// </summary>
public enum OracleStatementType
{
    /// <summary>Unknown statement type.</summary>
    Unknown = 0,

    /// <summary>SELECT query.</summary>
    Select = 1,

    /// <summary>INSERT statement.</summary>
    Insert = 2,

    /// <summary>UPDATE statement.</summary>
    Update = 3,

    /// <summary>DELETE statement.</summary>
    Delete = 4,

    /// <summary>CREATE DDL statement.</summary>
    Create = 5,

    /// <summary>DROP DDL statement.</summary>
    Drop = 6,

    /// <summary>ALTER DDL statement.</summary>
    Alter = 7,

    /// <summary>PL/SQL block.</summary>
    PlSqlBlock = 8,

    /// <summary>CALL statement.</summary>
    Call = 9,

    /// <summary>MERGE statement.</summary>
    Merge = 10,

    /// <summary>EXPLAIN PLAN statement.</summary>
    ExplainPlan = 11,

    /// <summary>COMMIT transaction control.</summary>
    Commit = 12,

    /// <summary>ROLLBACK transaction control.</summary>
    Rollback = 13
}

/// <summary>
/// Represents a bind parameter for prepared statements.
/// </summary>
public sealed class OracleBindParameter
{
    /// <summary>
    /// Gets or sets the parameter name (without colon prefix).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets or sets the parameter position (1-based).
    /// </summary>
    public int Position { get; init; }

    /// <summary>
    /// Gets or sets the Oracle type OID.
    /// </summary>
    public int TypeOid { get; set; } = OracleTypeOid.Varchar2;

    /// <summary>
    /// Gets or sets the maximum length for the parameter.
    /// </summary>
    public int MaxLength { get; set; } = 4000;

    /// <summary>
    /// Gets or sets the bound value.
    /// </summary>
    public byte[]? Value { get; set; }

    /// <summary>
    /// Gets or sets whether the parameter is an output parameter.
    /// </summary>
    public bool IsOutput { get; set; }

    /// <summary>
    /// Gets or sets whether the value is NULL.
    /// </summary>
    public bool IsNull { get; set; }
}

/// <summary>
/// Represents the state of an active Oracle connection.
/// </summary>
/// <remarks>
/// This class maintains all session-specific state including
/// authentication, cursors, transaction status, and NNE encryption.
/// Thread-safety: Each connection has its own state; no sharing.
/// </remarks>
public sealed class OracleConnectionState
{
    /// <summary>
    /// Gets the unique connection identifier.
    /// </summary>
    public required string ConnectionId { get; init; }

    /// <summary>
    /// Gets or sets the authenticated username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the connected schema/database.
    /// </summary>
    public string Schema { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the service name used for connection.
    /// </summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the client program name.
    /// </summary>
    public string ProgramName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the client machine name.
    /// </summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the client process ID.
    /// </summary>
    public string ProcessId { get; set; } = string.Empty;

    /// <summary>
    /// Gets the timestamp when the connection was established.
    /// </summary>
    public DateTime ConnectedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets whether the connection is authenticated.
    /// </summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// Gets or sets whether a transaction is active.
    /// </summary>
    public bool InTransaction { get; set; }

    /// <summary>
    /// Gets or sets whether the current transaction has failed.
    /// </summary>
    public bool TransactionFailed { get; set; }

    /// <summary>
    /// Gets the dictionary of open cursors by cursor ID.
    /// </summary>
    public ConcurrentDictionary<int, OracleCursor> Cursors { get; } = new();

    /// <summary>
    /// Gets or sets the next cursor ID to allocate.
    /// </summary>
    public int NextCursorId { get; set; } = 1;

    /// <summary>
    /// Gets the Oracle session ID (SID).
    /// </summary>
    public int SessionId { get; init; }

    /// <summary>
    /// Gets the Oracle serial number.
    /// </summary>
    public int SerialNumber { get; init; }

    /// <summary>
    /// Gets or sets the negotiated SDU size.
    /// </summary>
    public int SduSize { get; set; } = OracleProtocolConstants.DefaultSduSize;

    /// <summary>
    /// Gets or sets whether Native Network Encryption is enabled.
    /// </summary>
    public bool NneEnabled { get; set; }

    /// <summary>
    /// Gets or sets the negotiated encryption algorithm.
    /// </summary>
    public string? NneEncryptionAlgorithm { get; set; }

    /// <summary>
    /// Gets or sets the negotiated integrity algorithm.
    /// </summary>
    public string? NneIntegrityAlgorithm { get; set; }

    /// <summary>
    /// Gets or sets the encryption key for NNE.
    /// </summary>
    public byte[]? NneEncryptionKey { get; set; }

    /// <summary>
    /// Gets or sets the integrity key for NNE.
    /// </summary>
    public byte[]? NneIntegrityKey { get; set; }

    /// <summary>
    /// Gets the NLS session parameters.
    /// </summary>
    public Dictionary<string, string> NlsParameters { get; } = new()
    {
        ["NLS_LANGUAGE"] = "AMERICAN",
        ["NLS_TERRITORY"] = "AMERICA",
        ["NLS_CHARACTERSET"] = "AL32UTF8",
        ["NLS_DATE_FORMAT"] = "DD-MON-RR",
        ["NLS_TIMESTAMP_FORMAT"] = "DD-MON-RR HH.MI.SSXFF AM",
        ["NLS_NUMERIC_CHARACTERS"] = ".,"
    };

    /// <summary>
    /// Gets session-level parameters set by ALTER SESSION.
    /// </summary>
    public Dictionary<string, string> SessionParameters { get; } = new();

    /// <summary>
    /// Allocates a new cursor ID.
    /// </summary>
    /// <returns>A unique cursor identifier for this session.</returns>
    public int AllocateCursorId()
    {
        var oldValue = NextCursorId;
        NextCursorId = oldValue + 1;
        return NextCursorId;
    }
}

/// <summary>
/// Oracle query result containing rows and metadata.
/// </summary>
public sealed class OracleQueryResult
{
    /// <summary>
    /// Gets or sets whether the query returned no data.
    /// </summary>
    public bool IsEmpty { get; init; }

    /// <summary>
    /// Gets the column descriptions for the result set.
    /// </summary>
    public List<OracleColumnDescription> Columns { get; init; } = new();

    /// <summary>
    /// Gets the result rows, each row being a list of byte arrays (Oracle internal format).
    /// </summary>
    public List<List<byte[]?>> Rows { get; init; } = new();

    /// <summary>
    /// Gets or sets the number of rows affected by DML operations.
    /// </summary>
    public long RowsAffected { get; init; }

    /// <summary>
    /// Gets or sets the statement type that was executed.
    /// </summary>
    public OracleStatementType StatementType { get; init; } = OracleStatementType.Unknown;

    /// <summary>
    /// Gets or sets the error message if execution failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets or sets the Oracle error code if execution failed.
    /// </summary>
    public int? ErrorCode { get; init; }

    /// <summary>
    /// Gets or sets whether more rows are available (for fetch operations).
    /// </summary>
    public bool HasMoreRows { get; init; }
}

/// <summary>
/// Authentication challenge data for O5LOGON/O7LOGON/O8LOGON.
/// </summary>
public sealed class OracleAuthChallenge
{
    /// <summary>
    /// Gets the session key for authentication.
    /// </summary>
    public byte[] SessionKey { get; init; } = new byte[16];

    /// <summary>
    /// Gets the server-generated salt.
    /// </summary>
    public byte[] Salt { get; init; } = new byte[16];

    /// <summary>
    /// Gets the authentication iteration count for PBKDF2.
    /// </summary>
    public int Iterations { get; init; } = 10000;

    /// <summary>
    /// Gets the server-generated random nonce.
    /// </summary>
    public byte[] Nonce { get; init; } = new byte[24];

    /// <summary>
    /// Gets the authentication protocol version.
    /// </summary>
    public int AuthVersion { get; init; } = 8;

    /// <summary>
    /// Generates a new random authentication challenge.
    /// </summary>
    /// <param name="authVersion">The authentication version (5, 7, or 8).</param>
    /// <returns>A new authentication challenge with random values.</returns>
    public static OracleAuthChallenge Generate(int authVersion = 8)
    {
        var challenge = new OracleAuthChallenge
        {
            AuthVersion = authVersion,
            Iterations = authVersion >= 8 ? 10000 : 1
        };

        RandomNumberGenerator.Fill(challenge.SessionKey);
        RandomNumberGenerator.Fill(challenge.Salt);
        RandomNumberGenerator.Fill(challenge.Nonce);

        return challenge;
    }
}
