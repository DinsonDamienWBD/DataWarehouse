// <copyright file="OdbcTypes.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.OdbcDriver;

#region ODBC Return Codes

/// <summary>
/// ODBC return codes as defined in the ODBC 3.8 specification.
/// These codes indicate the success or failure of ODBC function calls.
/// </summary>
public enum SqlReturn : short
{
    /// <summary>
    /// Function completed successfully.
    /// </summary>
    Success = 0,

    /// <summary>
    /// Function completed successfully with informational warning.
    /// </summary>
    SuccessWithInfo = 1,

    /// <summary>
    /// No more data available.
    /// </summary>
    NoData = 100,

    /// <summary>
    /// Function returned an error.
    /// </summary>
    Error = -1,

    /// <summary>
    /// Invalid handle was passed to the function.
    /// </summary>
    InvalidHandle = -2,

    /// <summary>
    /// Need additional data.
    /// </summary>
    NeedData = 99,

    /// <summary>
    /// Function is still executing asynchronously.
    /// </summary>
    StillExecuting = 2,

    /// <summary>
    /// Parameter data is available at execution time.
    /// </summary>
    ParamDataAvailable = 101
}

#endregion

#region ODBC Handle Types

/// <summary>
/// ODBC handle types as defined in the ODBC 3.8 specification.
/// </summary>
public enum SqlHandleType : short
{
    /// <summary>
    /// Environment handle.
    /// </summary>
    Environment = 1,

    /// <summary>
    /// Connection handle.
    /// </summary>
    Connection = 2,

    /// <summary>
    /// Statement handle.
    /// </summary>
    Statement = 3,

    /// <summary>
    /// Descriptor handle.
    /// </summary>
    Descriptor = 4
}

#endregion

#region ODBC SQL Types

/// <summary>
/// SQL data types as defined in ODBC specification.
/// </summary>
public enum SqlDataType : short
{
    /// <summary>
    /// Unknown SQL type.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Character string.
    /// </summary>
    Char = 1,

    /// <summary>
    /// Numeric value.
    /// </summary>
    Numeric = 2,

    /// <summary>
    /// Decimal value.
    /// </summary>
    Decimal = 3,

    /// <summary>
    /// Integer value.
    /// </summary>
    Integer = 4,

    /// <summary>
    /// Small integer value.
    /// </summary>
    SmallInt = 5,

    /// <summary>
    /// Floating point value.
    /// </summary>
    Float = 6,

    /// <summary>
    /// Real (single precision float) value.
    /// </summary>
    Real = 7,

    /// <summary>
    /// Double precision floating point value.
    /// </summary>
    Double = 8,

    /// <summary>
    /// Date/time value (ODBC 2.x).
    /// </summary>
    DateTime = 9,

    /// <summary>
    /// Variable-length character string.
    /// </summary>
    VarChar = 12,

    /// <summary>
    /// Date value.
    /// </summary>
    TypeDate = 91,

    /// <summary>
    /// Time value.
    /// </summary>
    TypeTime = 92,

    /// <summary>
    /// Timestamp value.
    /// </summary>
    TypeTimestamp = 93,

    /// <summary>
    /// Unicode character string.
    /// </summary>
    WChar = -8,

    /// <summary>
    /// Unicode variable-length character string.
    /// </summary>
    WVarChar = -9,

    /// <summary>
    /// Unicode long variable-length character string.
    /// </summary>
    WLongVarChar = -10,

    /// <summary>
    /// Long variable-length character string.
    /// </summary>
    LongVarChar = -1,

    /// <summary>
    /// Binary data.
    /// </summary>
    Binary = -2,

    /// <summary>
    /// Variable-length binary data.
    /// </summary>
    VarBinary = -3,

    /// <summary>
    /// Long variable-length binary data.
    /// </summary>
    LongVarBinary = -4,

    /// <summary>
    /// Big integer value.
    /// </summary>
    BigInt = -5,

    /// <summary>
    /// Tiny integer value.
    /// </summary>
    TinyInt = -6,

    /// <summary>
    /// Bit value.
    /// </summary>
    Bit = -7,

    /// <summary>
    /// GUID value.
    /// </summary>
    Guid = -11
}

/// <summary>
/// C data types for ODBC binding.
/// </summary>
public enum SqlCType : short
{
    /// <summary>
    /// Default C type (determined by SQL type).
    /// </summary>
    Default = 99,

    /// <summary>
    /// Character string.
    /// </summary>
    Char = 1,

    /// <summary>
    /// Long value.
    /// </summary>
    Long = 4,

    /// <summary>
    /// Short value.
    /// </summary>
    Short = 5,

    /// <summary>
    /// Float value.
    /// </summary>
    Float = 7,

    /// <summary>
    /// Double value.
    /// </summary>
    Double = 8,

    /// <summary>
    /// Numeric value.
    /// </summary>
    Numeric = 2,

    /// <summary>
    /// Date structure.
    /// </summary>
    TypeDate = 91,

    /// <summary>
    /// Time structure.
    /// </summary>
    TypeTime = 92,

    /// <summary>
    /// Timestamp structure.
    /// </summary>
    TypeTimestamp = 93,

    /// <summary>
    /// Unicode character string.
    /// </summary>
    WChar = -8,

    /// <summary>
    /// Binary data.
    /// </summary>
    Binary = -2,

    /// <summary>
    /// Bit value.
    /// </summary>
    Bit = -7,

    /// <summary>
    /// Big integer (64-bit).
    /// </summary>
    SBigInt = -25,

    /// <summary>
    /// Unsigned big integer.
    /// </summary>
    UBigInt = -27,

    /// <summary>
    /// Signed tiny integer.
    /// </summary>
    STinyInt = -26,

    /// <summary>
    /// Unsigned tiny integer.
    /// </summary>
    UTinyInt = -28,

    /// <summary>
    /// Signed long.
    /// </summary>
    SLong = -16,

    /// <summary>
    /// Unsigned long.
    /// </summary>
    ULong = -18,

    /// <summary>
    /// Signed short.
    /// </summary>
    SShort = -15,

    /// <summary>
    /// Unsigned short.
    /// </summary>
    UShort = -17,

    /// <summary>
    /// GUID.
    /// </summary>
    Guid = -11
}

#endregion

#region ODBC Attributes

/// <summary>
/// Environment attributes for ODBC.
/// </summary>
public enum SqlEnvAttribute
{
    /// <summary>
    /// ODBC version.
    /// </summary>
    OdbcVersion = 200,

    /// <summary>
    /// Connection pooling.
    /// </summary>
    ConnectionPooling = 201,

    /// <summary>
    /// Connection pool match.
    /// </summary>
    CpMatch = 202,

    /// <summary>
    /// Output NTS (null-terminated string).
    /// </summary>
    OutputNts = 10001
}

/// <summary>
/// Connection attributes for ODBC.
/// </summary>
public enum SqlConnectAttribute
{
    /// <summary>
    /// Access mode (read-only or read-write).
    /// </summary>
    AccessMode = 101,

    /// <summary>
    /// Auto-commit mode.
    /// </summary>
    AutoCommit = 102,

    /// <summary>
    /// Login timeout in seconds.
    /// </summary>
    LoginTimeout = 103,

    /// <summary>
    /// Trace logging enabled.
    /// </summary>
    Trace = 104,

    /// <summary>
    /// Trace file path.
    /// </summary>
    TraceFile = 105,

    /// <summary>
    /// Translation library.
    /// </summary>
    TranslateLib = 106,

    /// <summary>
    /// Translation option.
    /// </summary>
    TranslateOption = 107,

    /// <summary>
    /// Transaction isolation level.
    /// </summary>
    TxnIsolation = 108,

    /// <summary>
    /// Current catalog (database).
    /// </summary>
    CurrentCatalog = 109,

    /// <summary>
    /// ODBC cursors.
    /// </summary>
    OdbcCursors = 110,

    /// <summary>
    /// Quiet mode (no prompts).
    /// </summary>
    QuietMode = 111,

    /// <summary>
    /// Packet size.
    /// </summary>
    PacketSize = 112,

    /// <summary>
    /// Connection timeout.
    /// </summary>
    ConnectionTimeout = 113,

    /// <summary>
    /// Disconnect behavior.
    /// </summary>
    DisconnectBehavior = 114,

    /// <summary>
    /// Asynchronous enable.
    /// </summary>
    AsyncEnable = 4,

    /// <summary>
    /// Connection dead check.
    /// </summary>
    ConnectionDead = 1209
}

/// <summary>
/// Statement attributes for ODBC.
/// </summary>
public enum SqlStmtAttribute
{
    /// <summary>
    /// Query timeout in seconds.
    /// </summary>
    QueryTimeout = 0,

    /// <summary>
    /// Maximum rows to return.
    /// </summary>
    MaxRows = 1,

    /// <summary>
    /// No scan optimization.
    /// </summary>
    Noscan = 2,

    /// <summary>
    /// Maximum string length.
    /// </summary>
    MaxLength = 3,

    /// <summary>
    /// Asynchronous enable.
    /// </summary>
    AsyncEnable = 4,

    /// <summary>
    /// Row bind type.
    /// </summary>
    RowBindType = 5,

    /// <summary>
    /// Cursor type.
    /// </summary>
    CursorType = 6,

    /// <summary>
    /// Concurrency.
    /// </summary>
    Concurrency = 7,

    /// <summary>
    /// Keyset size.
    /// </summary>
    KeysetSize = 8,

    /// <summary>
    /// Row number.
    /// </summary>
    RowNumber = 14,

    /// <summary>
    /// Simulate cursor.
    /// </summary>
    SimulateCursor = 10,

    /// <summary>
    /// Use bookmarks.
    /// </summary>
    UseBookmarks = 12,

    /// <summary>
    /// Cursor scrollable.
    /// </summary>
    CursorScrollable = -1,

    /// <summary>
    /// Cursor sensitivity.
    /// </summary>
    CursorSensitivity = -2,

    /// <summary>
    /// Application row descriptor.
    /// </summary>
    AppRowDesc = 10010,

    /// <summary>
    /// Application parameter descriptor.
    /// </summary>
    AppParamDesc = 10011,

    /// <summary>
    /// Implementation row descriptor.
    /// </summary>
    ImpRowDesc = 10012,

    /// <summary>
    /// Implementation parameter descriptor.
    /// </summary>
    ImpParamDesc = 10013,

    /// <summary>
    /// Fetch bookmark pointer.
    /// </summary>
    FetchBookmarkPtr = 16,

    /// <summary>
    /// Row bind offset pointer.
    /// </summary>
    RowBindOffsetPtr = 23,

    /// <summary>
    /// Row operation pointer.
    /// </summary>
    RowOperationPtr = 24,

    /// <summary>
    /// Row status pointer.
    /// </summary>
    RowStatusPtr = 25,

    /// <summary>
    /// Rows fetched pointer.
    /// </summary>
    RowsFetchedPtr = 26,

    /// <summary>
    /// Row array size.
    /// </summary>
    RowArraySize = 27,

    /// <summary>
    /// Parameter bind type.
    /// </summary>
    ParamBindType = 18,

    /// <summary>
    /// Parameter bind offset pointer.
    /// </summary>
    ParamBindOffsetPtr = 17,

    /// <summary>
    /// Parameter operation pointer.
    /// </summary>
    ParamOperationPtr = 19,

    /// <summary>
    /// Parameter status pointer.
    /// </summary>
    ParamStatusPtr = 20,

    /// <summary>
    /// Parameters processed pointer.
    /// </summary>
    ParamsProcessedPtr = 21,

    /// <summary>
    /// Parameter set size.
    /// </summary>
    ParamsetSize = 22
}

#endregion

#region ODBC Information Types

/// <summary>
/// Information types for SQLGetInfo.
/// </summary>
public enum SqlInfoType : ushort
{
    /// <summary>
    /// Driver name.
    /// </summary>
    DriverName = 6,

    /// <summary>
    /// Driver version.
    /// </summary>
    DriverVer = 7,

    /// <summary>
    /// ODBC version.
    /// </summary>
    DriverOdbcVer = 77,

    /// <summary>
    /// Data source name.
    /// </summary>
    DataSourceName = 2,

    /// <summary>
    /// Server name.
    /// </summary>
    ServerName = 13,

    /// <summary>
    /// Database name.
    /// </summary>
    DatabaseName = 16,

    /// <summary>
    /// DBMS name.
    /// </summary>
    DbmsName = 17,

    /// <summary>
    /// DBMS version.
    /// </summary>
    DbmsVer = 18,

    /// <summary>
    /// Maximum catalog name length.
    /// </summary>
    MaxCatalogNameLen = 34,

    /// <summary>
    /// Maximum column name length.
    /// </summary>
    MaxColumnNameLen = 30,

    /// <summary>
    /// Maximum cursor name length.
    /// </summary>
    MaxCursorNameLen = 31,

    /// <summary>
    /// Maximum schema name length.
    /// </summary>
    MaxSchemaNameLen = 32,

    /// <summary>
    /// Maximum table name length.
    /// </summary>
    MaxTableNameLen = 35,

    /// <summary>
    /// Maximum user name length.
    /// </summary>
    MaxUserNameLen = 107,

    /// <summary>
    /// Maximum identifier length.
    /// </summary>
    MaxIdentifierLen = 10005,

    /// <summary>
    /// User name.
    /// </summary>
    UserName = 47,

    /// <summary>
    /// Accessible tables.
    /// </summary>
    AccessibleTables = 19,

    /// <summary>
    /// Accessible procedures.
    /// </summary>
    AccessibleProcedures = 20,

    /// <summary>
    /// Catalog name.
    /// </summary>
    CatalogName = 42,

    /// <summary>
    /// Catalog name separator.
    /// </summary>
    CatalogNameSeparator = 41,

    /// <summary>
    /// Catalog term.
    /// </summary>
    CatalogTerm = 42,

    /// <summary>
    /// Schema term.
    /// </summary>
    SchemaTerm = 39,

    /// <summary>
    /// Table term.
    /// </summary>
    TableTerm = 45,

    /// <summary>
    /// Procedure term.
    /// </summary>
    ProcedureTerm = 40,

    /// <summary>
    /// Identifier quote character.
    /// </summary>
    IdentifierQuoteChar = 29,

    /// <summary>
    /// Search pattern escape.
    /// </summary>
    SearchPatternEscape = 14,

    /// <summary>
    /// String functions supported.
    /// </summary>
    StringFunctions = 50,

    /// <summary>
    /// Numeric functions supported.
    /// </summary>
    NumericFunctions = 49,

    /// <summary>
    /// Timedate functions supported.
    /// </summary>
    TimedateFunctions = 52,

    /// <summary>
    /// System functions supported.
    /// </summary>
    SystemFunctions = 51,

    /// <summary>
    /// Convert functions supported.
    /// </summary>
    ConvertFunctions = 48,

    /// <summary>
    /// SQL conformance.
    /// </summary>
    SqlConformance = 118,

    /// <summary>
    /// ODBC interface conformance.
    /// </summary>
    OdbcInterfaceConformance = 152,

    /// <summary>
    /// SQL92 predicates supported.
    /// </summary>
    Sql92Predicates = 160,

    /// <summary>
    /// SQL92 relational join operators.
    /// </summary>
    Sql92RelationalJoinOperators = 161,

    /// <summary>
    /// Datetime literals supported.
    /// </summary>
    DatetimeLiterals = 119,

    /// <summary>
    /// Cursor commit behavior.
    /// </summary>
    CursorCommitBehavior = 23,

    /// <summary>
    /// Cursor rollback behavior.
    /// </summary>
    CursorRollbackBehavior = 24,

    /// <summary>
    /// Transaction capable.
    /// </summary>
    TxnCapable = 46,

    /// <summary>
    /// Transaction isolation option.
    /// </summary>
    TxnIsolationOption = 72,

    /// <summary>
    /// Default transaction isolation.
    /// </summary>
    DefaultTxnIsolation = 26,

    /// <summary>
    /// Getdata extensions.
    /// </summary>
    GetdataExtensions = 81,

    /// <summary>
    /// Null collation.
    /// </summary>
    NullCollation = 85,

    /// <summary>
    /// Keywords.
    /// </summary>
    Keywords = 89,

    /// <summary>
    /// Special characters.
    /// </summary>
    SpecialCharacters = 94,

    /// <summary>
    /// Maximum statement length.
    /// </summary>
    MaxStatementLen = 105,

    /// <summary>
    /// Active connections.
    /// </summary>
    ActiveConnections = 0,

    /// <summary>
    /// Active statements.
    /// </summary>
    ActiveStatements = 1,

    /// <summary>
    /// Data source read only.
    /// </summary>
    DataSourceReadOnly = 25,

    /// <summary>
    /// Multiple result sets.
    /// </summary>
    MultResultSets = 36,

    /// <summary>
    /// Batch row count.
    /// </summary>
    BatchRowCount = 120,

    /// <summary>
    /// Batch support.
    /// </summary>
    BatchSupport = 121,

    /// <summary>
    /// Scroll options.
    /// </summary>
    ScrollOptions = 44,

    /// <summary>
    /// Dynamic cursor attributes 1.
    /// </summary>
    DynamicCursorAttributes1 = 144,

    /// <summary>
    /// Dynamic cursor attributes 2.
    /// </summary>
    DynamicCursorAttributes2 = 145,

    /// <summary>
    /// Forward only cursor attributes 1.
    /// </summary>
    ForwardOnlyCursorAttributes1 = 146,

    /// <summary>
    /// Forward only cursor attributes 2.
    /// </summary>
    ForwardOnlyCursorAttributes2 = 147,

    /// <summary>
    /// Keyset cursor attributes 1.
    /// </summary>
    KeysetCursorAttributes1 = 150,

    /// <summary>
    /// Keyset cursor attributes 2.
    /// </summary>
    KeysetCursorAttributes2 = 151,

    /// <summary>
    /// Static cursor attributes 1.
    /// </summary>
    StaticCursorAttributes1 = 167,

    /// <summary>
    /// Static cursor attributes 2.
    /// </summary>
    StaticCursorAttributes2 = 168
}

#endregion

#region Descriptor Field Types

/// <summary>
/// Descriptor field identifiers for SQLColAttribute/SQLDescribeCol.
/// Based on ODBC 3.8 specification SQL_DESC_* constants.
/// </summary>
public enum SqlDescField : short
{
    /// <summary>
    /// Column count.
    /// </summary>
    Count = 1001,

    /// <summary>
    /// Column name.
    /// </summary>
    Name = 1011,

    /// <summary>
    /// Data type.
    /// </summary>
    Type = 1002,

    /// <summary>
    /// Column length.
    /// </summary>
    Length = 1003,

    /// <summary>
    /// Precision.
    /// </summary>
    Precision = 1005,

    /// <summary>
    /// Scale.
    /// </summary>
    Scale = 1006,

    /// <summary>
    /// Nullable.
    /// </summary>
    Nullable = 1008,

    /// <summary>
    /// Auto-unique value.
    /// </summary>
    AutoUniqueValue = 11,

    /// <summary>
    /// Base column name.
    /// </summary>
    BaseColumnName = 22,

    /// <summary>
    /// Base table name.
    /// </summary>
    BaseTableName = 23,

    /// <summary>
    /// Case sensitive.
    /// </summary>
    CaseSensitive = 12,

    /// <summary>
    /// Catalog name.
    /// </summary>
    CatalogName = 17,

    /// <summary>
    /// Concise type.
    /// </summary>
    ConciseType = 1007,

    /// <summary>
    /// Octet length.
    /// </summary>
    OctetLength = 1013,

    /// <summary>
    /// Display size.
    /// </summary>
    DisplaySize = 1016,

    /// <summary>
    /// Fixed precision scale.
    /// </summary>
    FixedPrecScale = 9,

    /// <summary>
    /// Label.
    /// </summary>
    Label = 18,

    /// <summary>
    /// Literal prefix.
    /// </summary>
    LiteralPrefix = 27,

    /// <summary>
    /// Literal suffix.
    /// </summary>
    LiteralSuffix = 28,

    /// <summary>
    /// Local type name.
    /// </summary>
    LocalTypeName = 29,

    /// <summary>
    /// Numeric precision radix.
    /// </summary>
    NumPrecRadix = 32,

    /// <summary>
    /// Schema name.
    /// </summary>
    SchemaName = 16,

    /// <summary>
    /// Searchable.
    /// </summary>
    Searchable = 13,

    /// <summary>
    /// Table name.
    /// </summary>
    TableName = 15,

    /// <summary>
    /// Type name.
    /// </summary>
    TypeName = 14,

    /// <summary>
    /// Unnamed.
    /// </summary>
    Unnamed = 1012,

    /// <summary>
    /// Unsigned.
    /// </summary>
    Unsigned = 8,

    /// <summary>
    /// Updatable.
    /// </summary>
    Updatable = 10
}

#endregion

#region SQLSTATE Codes

/// <summary>
/// Standard SQLSTATE codes for ODBC error reporting.
/// </summary>
public static class SqlState
{
    /// <summary>
    /// Successful completion.
    /// </summary>
    public const string Success = "00000";

    /// <summary>
    /// General warning.
    /// </summary>
    public const string GeneralWarning = "01000";

    /// <summary>
    /// Cursor operation conflict.
    /// </summary>
    public const string CursorOperationConflict = "01001";

    /// <summary>
    /// Disconnect error.
    /// </summary>
    public const string DisconnectError = "01002";

    /// <summary>
    /// Data truncated.
    /// </summary>
    public const string DataTruncated = "01004";

    /// <summary>
    /// Privilege not revoked.
    /// </summary>
    public const string PrivilegeNotRevoked = "01006";

    /// <summary>
    /// Privilege not granted.
    /// </summary>
    public const string PrivilegeNotGranted = "01007";

    /// <summary>
    /// Invalid connection string attribute.
    /// </summary>
    public const string InvalidConnectionStringAttr = "01S00";

    /// <summary>
    /// No data found.
    /// </summary>
    public const string NoData = "02000";

    /// <summary>
    /// Connection exception.
    /// </summary>
    public const string ConnectionException = "08000";

    /// <summary>
    /// Client unable to establish connection.
    /// </summary>
    public const string ClientUnableToConnect = "08001";

    /// <summary>
    /// Connection name in use.
    /// </summary>
    public const string ConnectionNameInUse = "08002";

    /// <summary>
    /// Connection does not exist.
    /// </summary>
    public const string ConnectionNotExist = "08003";

    /// <summary>
    /// Server rejected connection.
    /// </summary>
    public const string ServerRejectedConnection = "08004";

    /// <summary>
    /// Connection failure.
    /// </summary>
    public const string ConnectionFailure = "08006";

    /// <summary>
    /// Transaction resolution unknown.
    /// </summary>
    public const string TransactionResolutionUnknown = "08007";

    /// <summary>
    /// Communication link failure.
    /// </summary>
    public const string CommunicationLinkFailure = "08S01";

    /// <summary>
    /// Feature not supported.
    /// </summary>
    public const string FeatureNotSupported = "0A000";

    /// <summary>
    /// Cardinality violation.
    /// </summary>
    public const string CardinalityViolation = "21000";

    /// <summary>
    /// String data right truncation.
    /// </summary>
    public const string StringDataRightTrunc = "22001";

    /// <summary>
    /// Indicator variable required.
    /// </summary>
    public const string IndicatorRequired = "22002";

    /// <summary>
    /// Numeric value out of range.
    /// </summary>
    public const string NumericOutOfRange = "22003";

    /// <summary>
    /// Invalid datetime format.
    /// </summary>
    public const string InvalidDatetimeFormat = "22007";

    /// <summary>
    /// Datetime field overflow.
    /// </summary>
    public const string DatetimeOverflow = "22008";

    /// <summary>
    /// Division by zero.
    /// </summary>
    public const string DivisionByZero = "22012";

    /// <summary>
    /// Invalid character value for cast.
    /// </summary>
    public const string InvalidCharacterValue = "22018";

    /// <summary>
    /// Invalid escape character.
    /// </summary>
    public const string InvalidEscapeChar = "22019";

    /// <summary>
    /// Invalid escape sequence.
    /// </summary>
    public const string InvalidEscapeSeq = "22025";

    /// <summary>
    /// String length mismatch.
    /// </summary>
    public const string StringLengthMismatch = "22026";

    /// <summary>
    /// Integrity constraint violation.
    /// </summary>
    public const string IntegrityConstraintViolation = "23000";

    /// <summary>
    /// Invalid cursor state.
    /// </summary>
    public const string InvalidCursorState = "24000";

    /// <summary>
    /// Invalid transaction state.
    /// </summary>
    public const string InvalidTransactionState = "25000";

    /// <summary>
    /// Transaction is active.
    /// </summary>
    public const string TransactionActive = "25001";

    /// <summary>
    /// Invalid authorization specification.
    /// </summary>
    public const string InvalidAuthSpec = "28000";

    /// <summary>
    /// Invalid cursor name.
    /// </summary>
    public const string InvalidCursorName = "34000";

    /// <summary>
    /// Syntax error or access violation.
    /// </summary>
    public const string SyntaxErrorOrAccessViolation = "42000";

    /// <summary>
    /// Base table or view not found.
    /// </summary>
    public const string BaseTableNotFound = "42S02";

    /// <summary>
    /// Index not found.
    /// </summary>
    public const string IndexNotFound = "42S12";

    /// <summary>
    /// Column not found.
    /// </summary>
    public const string ColumnNotFound = "42S22";

    /// <summary>
    /// General error.
    /// </summary>
    public const string GeneralError = "HY000";

    /// <summary>
    /// Memory allocation error.
    /// </summary>
    public const string MemoryAllocationError = "HY001";

    /// <summary>
    /// Invalid application buffer type.
    /// </summary>
    public const string InvalidAppBufferType = "HY003";

    /// <summary>
    /// Invalid SQL data type.
    /// </summary>
    public const string InvalidSqlDataType = "HY004";

    /// <summary>
    /// Operation canceled.
    /// </summary>
    public const string OperationCanceled = "HY008";

    /// <summary>
    /// Invalid argument value.
    /// </summary>
    public const string InvalidArgumentValue = "HY009";

    /// <summary>
    /// Function sequence error.
    /// </summary>
    public const string FunctionSequenceError = "HY010";

    /// <summary>
    /// Attribute cannot be set now.
    /// </summary>
    public const string AttributeCannotBeSetNow = "HY011";

    /// <summary>
    /// Invalid transaction operation code.
    /// </summary>
    public const string InvalidTransactionOpCode = "HY012";

    /// <summary>
    /// Memory management error.
    /// </summary>
    public const string MemoryManagementError = "HY013";

    /// <summary>
    /// Invalid use of null pointer.
    /// </summary>
    public const string InvalidNullPointer = "HY014";

    /// <summary>
    /// Invalid string or buffer length.
    /// </summary>
    public const string InvalidStringOrBufferLength = "HY090";

    /// <summary>
    /// Invalid descriptor field identifier.
    /// </summary>
    public const string InvalidDescriptorFieldId = "HY091";

    /// <summary>
    /// Invalid attribute or option identifier.
    /// </summary>
    public const string InvalidAttrOrOptionId = "HY092";

    /// <summary>
    /// Invalid parameter number.
    /// </summary>
    public const string InvalidParameterNumber = "HY093";

    /// <summary>
    /// Invalid function argument.
    /// </summary>
    public const string InvalidFunctionArgument = "HY095";

    /// <summary>
    /// Invalid information type.
    /// </summary>
    public const string InvalidInfoType = "HY096";

    /// <summary>
    /// Column type out of range.
    /// </summary>
    public const string ColumnTypeOutOfRange = "HY097";

    /// <summary>
    /// Scope type out of range.
    /// </summary>
    public const string ScopeTypeOutOfRange = "HY098";

    /// <summary>
    /// Nullable type out of range.
    /// </summary>
    public const string NullableTypeOutOfRange = "HY099";

    /// <summary>
    /// Uniqueness option type out of range.
    /// </summary>
    public const string UniquenessOptionOutOfRange = "HY100";

    /// <summary>
    /// Accuracy option type out of range.
    /// </summary>
    public const string AccuracyOptionOutOfRange = "HY101";

    /// <summary>
    /// Table type out of range.
    /// </summary>
    public const string TableTypeOutOfRange = "HY102";

    /// <summary>
    /// Invalid retrieval code.
    /// </summary>
    public const string InvalidRetrievalCode = "HY103";

    /// <summary>
    /// Invalid precision or scale value.
    /// </summary>
    public const string InvalidPrecisionOrScale = "HY104";

    /// <summary>
    /// Invalid parameter type.
    /// </summary>
    public const string InvalidParameterType = "HY105";

    /// <summary>
    /// Fetch type out of range.
    /// </summary>
    public const string FetchTypeOutOfRange = "HY106";

    /// <summary>
    /// Row value out of range.
    /// </summary>
    public const string RowValueOutOfRange = "HY107";

    /// <summary>
    /// Invalid cursor position.
    /// </summary>
    public const string InvalidCursorPosition = "HY109";

    /// <summary>
    /// Invalid driver completion.
    /// </summary>
    public const string InvalidDriverCompletion = "HY110";

    /// <summary>
    /// Invalid bookmark value.
    /// </summary>
    public const string InvalidBookmarkValue = "HY111";

    /// <summary>
    /// Optional feature not implemented.
    /// </summary>
    public const string OptionalFeatureNotImpl = "HYC00";

    /// <summary>
    /// Timeout expired.
    /// </summary>
    public const string TimeoutExpired = "HYT00";

    /// <summary>
    /// Connection timeout expired.
    /// </summary>
    public const string ConnectionTimeoutExpired = "HYT01";

    /// <summary>
    /// Driver does not support this function.
    /// </summary>
    public const string DriverNotSupport = "IM001";

    /// <summary>
    /// Data source name not found.
    /// </summary>
    public const string DataSourceNameNotFound = "IM002";

    /// <summary>
    /// Driver could not be loaded.
    /// </summary>
    public const string DriverLoadError = "IM003";

    /// <summary>
    /// Driver SQLAllocHandle on environment failed.
    /// </summary>
    public const string DriverAllocEnvFailed = "IM004";

    /// <summary>
    /// Driver SQLAllocHandle on connection failed.
    /// </summary>
    public const string DriverAllocConnFailed = "IM005";

    /// <summary>
    /// Driver SQLSetConnectAttr failed.
    /// </summary>
    public const string DriverSetConnAttrFailed = "IM006";

    /// <summary>
    /// No data source or driver specified.
    /// </summary>
    public const string NoDataSourceOrDriver = "IM007";

    /// <summary>
    /// Dialog failed.
    /// </summary>
    public const string DialogFailed = "IM008";

    /// <summary>
    /// Unable to load translation DLL.
    /// </summary>
    public const string TranslationDllLoadError = "IM009";

    /// <summary>
    /// Data source name too long.
    /// </summary>
    public const string DataSourceNameTooLong = "IM010";

    /// <summary>
    /// Driver name too long.
    /// </summary>
    public const string DriverNameTooLong = "IM011";

    /// <summary>
    /// Driver keyword syntax error.
    /// </summary>
    public const string DriverKeywordSyntaxError = "IM012";

    /// <summary>
    /// Trace file error.
    /// </summary>
    public const string TraceFileError = "IM013";

    /// <summary>
    /// Invalid name of file DSN.
    /// </summary>
    public const string InvalidFileDsnName = "IM014";

    /// <summary>
    /// Corrupt file data source.
    /// </summary>
    public const string CorruptFileDsn = "IM015";
}

#endregion

#region Configuration

/// <summary>
/// Configuration options for the ODBC driver plugin.
/// </summary>
public sealed class OdbcDriverConfig
{
    /// <summary>
    /// Gets or sets the driver name as reported to ODBC applications.
    /// </summary>
    public string DriverName { get; set; } = "DataWarehouse ODBC Driver";

    /// <summary>
    /// Gets or sets the driver version.
    /// </summary>
    public string DriverVersion { get; set; } = "01.00.0000";

    /// <summary>
    /// Gets or sets the ODBC version supported (default: 3.80).
    /// </summary>
    public string OdbcVersion { get; set; } = "03.80";

    /// <summary>
    /// Gets or sets the default database name.
    /// </summary>
    public string DefaultDatabase { get; set; } = "datawarehouse";

    /// <summary>
    /// Gets or sets the default schema name.
    /// </summary>
    public string DefaultSchema { get; set; } = "public";

    /// <summary>
    /// Gets or sets the maximum number of concurrent connections.
    /// </summary>
    public int MaxConnections { get; set; } = 100;

    /// <summary>
    /// Gets or sets the default query timeout in seconds.
    /// </summary>
    public int DefaultQueryTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Gets or sets the default connection timeout in seconds.
    /// </summary>
    public int DefaultConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets whether to enable tracing.
    /// </summary>
    public bool EnableTracing { get; set; }

    /// <summary>
    /// Gets or sets the trace file path.
    /// </summary>
    public string? TraceFile { get; set; }

    /// <summary>
    /// Gets or sets the maximum string length for character data.
    /// </summary>
    public int MaxStringLength { get; set; } = 65535;

    /// <summary>
    /// Gets or sets whether to use Unicode by default.
    /// </summary>
    public bool UseUnicode { get; set; } = true;
}

#endregion

#region Diagnostic Record

/// <summary>
/// Represents a diagnostic record for ODBC error reporting.
/// </summary>
public sealed class OdbcDiagnosticRecord
{
    /// <summary>
    /// Gets or sets the SQLSTATE code.
    /// </summary>
    public required string SqlState { get; init; }

    /// <summary>
    /// Gets or sets the native error code.
    /// </summary>
    public int NativeError { get; init; }

    /// <summary>
    /// Gets or sets the error message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets or sets the server name.
    /// </summary>
    public string? ServerName { get; init; }

    /// <summary>
    /// Gets or sets the connection name.
    /// </summary>
    public string? ConnectionName { get; init; }

    /// <summary>
    /// Gets or sets the row number.
    /// </summary>
    public long RowNumber { get; init; } = -1;

    /// <summary>
    /// Gets or sets the column number.
    /// </summary>
    public int ColumnNumber { get; init; } = -1;

    /// <summary>
    /// Gets the timestamp when the diagnostic was created.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

#endregion

#region Column Metadata

/// <summary>
/// Column metadata for result sets.
/// </summary>
public sealed class OdbcColumnInfo
{
    /// <summary>
    /// Gets or sets the column name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets or sets the column ordinal (1-based).
    /// </summary>
    public int Ordinal { get; init; }

    /// <summary>
    /// Gets or sets the SQL data type.
    /// </summary>
    public SqlDataType SqlType { get; init; }

    /// <summary>
    /// Gets or sets the column size (precision for numeric types, length for strings).
    /// </summary>
    public int ColumnSize { get; init; }

    /// <summary>
    /// Gets or sets the number of decimal digits (scale).
    /// </summary>
    public short DecimalDigits { get; init; }

    /// <summary>
    /// Gets or sets whether the column is nullable.
    /// </summary>
    public bool IsNullable { get; init; } = true;

    /// <summary>
    /// Gets or sets the base table name.
    /// </summary>
    public string? BaseTableName { get; init; }

    /// <summary>
    /// Gets or sets the base column name.
    /// </summary>
    public string? BaseColumnName { get; init; }

    /// <summary>
    /// Gets or sets the catalog name.
    /// </summary>
    public string? CatalogName { get; init; }

    /// <summary>
    /// Gets or sets the schema name.
    /// </summary>
    public string? SchemaName { get; init; }

    /// <summary>
    /// Gets or sets whether the column is auto-increment.
    /// </summary>
    public bool IsAutoIncrement { get; init; }

    /// <summary>
    /// Gets or sets whether the column is case-sensitive.
    /// </summary>
    public bool IsCaseSensitive { get; init; }

    /// <summary>
    /// Gets or sets whether the column is searchable.
    /// </summary>
    public bool IsSearchable { get; init; } = true;

    /// <summary>
    /// Gets or sets whether the column is unsigned.
    /// </summary>
    public bool IsUnsigned { get; init; }

    /// <summary>
    /// Gets or sets whether the column is updatable.
    /// </summary>
    public bool IsUpdatable { get; init; }

    /// <summary>
    /// Gets or sets the display size.
    /// </summary>
    public int DisplaySize { get; init; }

    /// <summary>
    /// Gets or sets the octet length.
    /// </summary>
    public int OctetLength { get; init; }

    /// <summary>
    /// Gets or sets the type name.
    /// </summary>
    public string TypeName { get; init; } = "VARCHAR";
}

#endregion

#region Query Result

/// <summary>
/// Represents a query result from the DataWarehouse.
/// </summary>
public sealed class OdbcQueryResult
{
    /// <summary>
    /// Gets or sets whether this is an empty result.
    /// </summary>
    public bool IsEmpty { get; init; }

    /// <summary>
    /// Gets or sets whether this query produced a result set.
    /// </summary>
    public bool HasResultSet { get; init; }

    /// <summary>
    /// Gets or sets the columns in the result set.
    /// </summary>
    public List<OdbcColumnInfo> Columns { get; init; } = new();

    /// <summary>
    /// Gets or sets the rows in the result set.
    /// </summary>
    public List<object?[]> Rows { get; init; } = new();

    /// <summary>
    /// Gets or sets the number of rows affected by an UPDATE/INSERT/DELETE.
    /// </summary>
    public long RowsAffected { get; init; } = -1;

    /// <summary>
    /// Gets or sets the error message if an error occurred.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets or sets the SQLSTATE if an error occurred.
    /// </summary>
    public string? ErrorSqlState { get; init; }
}

#endregion

#region Parameter Binding

/// <summary>
/// Represents a bound parameter for prepared statements.
/// </summary>
public sealed class OdbcBoundParameter
{
    /// <summary>
    /// Gets or sets the parameter number (1-based).
    /// </summary>
    public int ParameterNumber { get; init; }

    /// <summary>
    /// Gets or sets the input/output type.
    /// </summary>
    public ParameterDirection Direction { get; init; } = ParameterDirection.Input;

    /// <summary>
    /// Gets or sets the value type (C type).
    /// </summary>
    public SqlCType ValueType { get; init; }

    /// <summary>
    /// Gets or sets the parameter type (SQL type).
    /// </summary>
    public SqlDataType ParameterType { get; init; }

    /// <summary>
    /// Gets or sets the column size.
    /// </summary>
    public int ColumnSize { get; init; }

    /// <summary>
    /// Gets or sets the decimal digits.
    /// </summary>
    public short DecimalDigits { get; init; }

    /// <summary>
    /// Gets or sets the bound value.
    /// </summary>
    public object? Value { get; set; }

    /// <summary>
    /// Gets or sets the buffer length.
    /// </summary>
    public int BufferLength { get; init; }

    /// <summary>
    /// Gets or sets the indicator (for NULL values and data-at-execution).
    /// </summary>
    public nint Indicator { get; set; }
}

/// <summary>
/// Parameter direction for bound parameters.
/// </summary>
public enum ParameterDirection
{
    /// <summary>
    /// Input parameter.
    /// </summary>
    Input = 1,

    /// <summary>
    /// Input/Output parameter.
    /// </summary>
    InputOutput = 2,

    /// <summary>
    /// Output parameter.
    /// </summary>
    Output = 4,

    /// <summary>
    /// Return value.
    /// </summary>
    ReturnValue = 5
}

#endregion

#region Column Binding

/// <summary>
/// Represents a bound column for result fetching.
/// </summary>
public sealed class OdbcBoundColumn
{
    /// <summary>
    /// Gets or sets the column number (1-based).
    /// </summary>
    public int ColumnNumber { get; init; }

    /// <summary>
    /// Gets or sets the target type (C type).
    /// </summary>
    public SqlCType TargetType { get; init; }

    /// <summary>
    /// Gets or sets the buffer length.
    /// </summary>
    public int BufferLength { get; init; }

    /// <summary>
    /// Gets or sets the value after fetch.
    /// </summary>
    public object? Value { get; set; }

    /// <summary>
    /// Gets or sets the indicator (length or NULL indicator).
    /// </summary>
    public nint Indicator { get; set; }
}

#endregion

#region Nullable Constants

/// <summary>
/// Nullable constants for ODBC.
/// </summary>
public enum SqlNullable : short
{
    /// <summary>
    /// Column does not allow NULL values.
    /// </summary>
    NoNulls = 0,

    /// <summary>
    /// Column allows NULL values.
    /// </summary>
    Nullable = 1,

    /// <summary>
    /// Unknown if column allows NULL values.
    /// </summary>
    Unknown = 2
}

#endregion

#region Searchable Constants

/// <summary>
/// Searchable constants for column attributes.
/// </summary>
public enum SqlSearchable : short
{
    /// <summary>
    /// Column cannot be used in a WHERE clause.
    /// </summary>
    Unsearchable = 0,

    /// <summary>
    /// Column can be used in a WHERE clause with LIKE.
    /// </summary>
    LikeOnly = 1,

    /// <summary>
    /// Column can be used in a WHERE clause with any operator except LIKE.
    /// </summary>
    AllExceptLike = 2,

    /// <summary>
    /// Column can be used in a WHERE clause with any operator.
    /// </summary>
    Searchable = 3
}

#endregion
