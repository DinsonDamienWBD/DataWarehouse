// <copyright file="OdbcUnicodeApi.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using DataWarehouse.Plugins.OdbcDriver.Handles;
using System.Text;

namespace DataWarehouse.Plugins.OdbcDriver.Api;

/// <summary>
/// Implements the Unicode (W-suffix) variants of ODBC 3.8 API functions.
/// These functions accept and return Unicode strings for international character support.
/// All methods delegate to the ANSI API with appropriate string conversion.
/// Thread-safe: All methods are thread-safe.
/// </summary>
public sealed class OdbcUnicodeApi
{
    private readonly OdbcApi _ansiApi;
    private readonly Encoding _encoding = Encoding.Unicode;

    /// <summary>
    /// Initializes a new instance of the <see cref="OdbcUnicodeApi"/> class.
    /// </summary>
    /// <param name="ansiApi">The ANSI API instance to delegate to.</param>
    public OdbcUnicodeApi(OdbcApi ansiApi)
    {
        _ansiApi = ansiApi ?? throw new ArgumentNullException(nameof(ansiApi));
    }

    #region Connection Functions (SQLConnectW, SQLDriverConnectW)

    /// <summary>
    /// Establishes a connection to a data source using Unicode strings.
    /// Implements SQLConnectW from ODBC 3.8 specification.
    /// </summary>
    /// <param name="connectionHandle">The connection handle.</param>
    /// <param name="serverName">The data source name (DSN) in Unicode.</param>
    /// <param name="userName">The user identifier in Unicode.</param>
    /// <param name="authentication">The authentication string (password) in Unicode.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLConnectW(
        nint connectionHandle,
        string serverName,
        string userName,
        string authentication)
    {
        // Unicode strings are already native in .NET, just delegate
        return _ansiApi.SQLConnect(connectionHandle, serverName, userName, authentication);
    }

    /// <summary>
    /// Establishes a connection using a Unicode connection string.
    /// Implements SQLDriverConnectW from ODBC 3.8 specification.
    /// </summary>
    /// <param name="connectionHandle">The connection handle.</param>
    /// <param name="windowHandle">The parent window handle (for dialog prompts).</param>
    /// <param name="inConnectionString">The input connection string in Unicode.</param>
    /// <param name="outConnectionString">The completed connection string in Unicode.</param>
    /// <param name="driverCompletion">Driver completion option.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_NO_DATA, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLDriverConnectW(
        nint connectionHandle,
        nint windowHandle,
        string inConnectionString,
        out string outConnectionString,
        DriverCompletion driverCompletion)
    {
        return _ansiApi.SQLDriverConnect(
            connectionHandle,
            windowHandle,
            inConnectionString,
            out outConnectionString,
            driverCompletion);
    }

    #endregion

    #region Statement Execution (SQLExecDirectW, SQLPrepareW)

    /// <summary>
    /// Executes a Unicode SQL statement directly.
    /// Implements SQLExecDirectW from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="statementText">The SQL statement in Unicode.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_NEED_DATA, SQL_NO_DATA, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLExecDirectW(nint statementHandle, string statementText)
    {
        return _ansiApi.SQLExecDirect(statementHandle, statementText);
    }

    /// <summary>
    /// Prepares a Unicode SQL statement for execution.
    /// Implements SQLPrepareW from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="statementText">The SQL statement in Unicode.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLPrepareW(nint statementHandle, string statementText)
    {
        return _ansiApi.SQLPrepare(statementHandle, statementText);
    }

    #endregion

    #region Column Metadata (SQLDescribeColW, SQLColAttributeW)

    /// <summary>
    /// Describes a column in the result set with Unicode column names.
    /// Implements SQLDescribeColW from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="columnNumber">The 1-based column number.</param>
    /// <param name="columnName">The column name in Unicode.</param>
    /// <param name="dataType">The SQL data type.</param>
    /// <param name="columnSize">The column size.</param>
    /// <param name="decimalDigits">The decimal digits (scale).</param>
    /// <param name="nullable">Whether the column allows NULL values.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLDescribeColW(
        nint statementHandle,
        int columnNumber,
        out string columnName,
        out SqlDataType dataType,
        out int columnSize,
        out short decimalDigits,
        out SqlNullable nullable)
    {
        return _ansiApi.SQLDescribeCol(
            statementHandle,
            columnNumber,
            out columnName,
            out dataType,
            out columnSize,
            out decimalDigits,
            out nullable);
    }

    /// <summary>
    /// Gets a column attribute with Unicode string values.
    /// Implements SQLColAttributeW from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="columnNumber">The 1-based column number.</param>
    /// <param name="fieldIdentifier">The field identifier.</param>
    /// <param name="characterAttribute">The character attribute (for string fields).</param>
    /// <param name="numericAttribute">The numeric attribute (for numeric fields).</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLColAttributeW(
        nint statementHandle,
        int columnNumber,
        SqlDescField fieldIdentifier,
        out string? characterAttribute,
        out nint numericAttribute)
    {
        characterAttribute = null;
        numericAttribute = nint.Zero;

        var handleManager = HandleManager.Instance;
        var stmt = handleManager.GetStatementHandle(statementHandle);
        if (stmt == null)
        {
            return SqlReturn.InvalidHandle;
        }

        if (columnNumber < 1 || columnNumber > stmt.ColumnCount)
        {
            return SqlReturn.Error;
        }

        var col = stmt.Columns[columnNumber - 1];

        switch (fieldIdentifier)
        {
            case SqlDescField.Name:
            case SqlDescField.Label:
                characterAttribute = col.Name;
                break;

            case SqlDescField.TypeName:
                characterAttribute = col.TypeName;
                break;

            case SqlDescField.TableName:
            case SqlDescField.BaseTableName:
                characterAttribute = col.BaseTableName;
                break;

            case SqlDescField.BaseColumnName:
                characterAttribute = col.BaseColumnName;
                break;

            case SqlDescField.CatalogName:
                characterAttribute = col.CatalogName;
                break;

            case SqlDescField.SchemaName:
                characterAttribute = col.SchemaName;
                break;

            case SqlDescField.Type:
            case SqlDescField.ConciseType:
                numericAttribute = (int)col.SqlType;
                break;

            case SqlDescField.Length:
            case SqlDescField.OctetLength:
                numericAttribute = col.OctetLength;
                break;

            case SqlDescField.Precision:
                numericAttribute = col.ColumnSize;
                break;

            case SqlDescField.Scale:
                numericAttribute = col.DecimalDigits;
                break;

            case SqlDescField.Nullable:
                numericAttribute = col.IsNullable ? (int)SqlNullable.Nullable : (int)SqlNullable.NoNulls;
                break;

            case SqlDescField.DisplaySize:
                numericAttribute = col.DisplaySize;
                break;

            case SqlDescField.AutoUniqueValue:
                numericAttribute = col.IsAutoIncrement ? 1 : 0;
                break;

            case SqlDescField.CaseSensitive:
                numericAttribute = col.IsCaseSensitive ? 1 : 0;
                break;

            case SqlDescField.Searchable:
                numericAttribute = col.IsSearchable ? (int)SqlSearchable.Searchable : (int)SqlSearchable.Unsearchable;
                break;

            case SqlDescField.Unsigned:
                numericAttribute = col.IsUnsigned ? 1 : 0;
                break;

            case SqlDescField.Updatable:
                numericAttribute = col.IsUpdatable ? 1 : 0;
                break;

            default:
                return SqlReturn.Error;
        }

        return SqlReturn.Success;
    }

    #endregion

    #region Information Functions (SQLGetInfoW, SQLGetDiagRecW)

    /// <summary>
    /// Gets information about the driver and data source with Unicode strings.
    /// Implements SQLGetInfoW from ODBC 3.8 specification.
    /// </summary>
    /// <param name="connectionHandle">The connection handle.</param>
    /// <param name="infoType">The type of information to retrieve.</param>
    /// <param name="infoValue">The information value.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLGetInfoW(nint connectionHandle, SqlInfoType infoType, out object? infoValue)
    {
        return _ansiApi.SQLGetInfo(connectionHandle, infoType, out infoValue);
    }

    /// <summary>
    /// Gets diagnostic information with Unicode message text.
    /// Implements SQLGetDiagRecW from ODBC 3.8 specification.
    /// </summary>
    /// <param name="handleType">The handle type.</param>
    /// <param name="handle">The handle.</param>
    /// <param name="recordNumber">The 1-based record number.</param>
    /// <param name="sqlState">The SQLSTATE code.</param>
    /// <param name="nativeError">The native error code.</param>
    /// <param name="message">The error message in Unicode.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_NO_DATA, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLGetDiagRecW(
        SqlHandleType handleType,
        nint handle,
        int recordNumber,
        out string sqlState,
        out int nativeError,
        out string message)
    {
        return _ansiApi.SQLGetDiagRec(
            handleType,
            handle,
            recordNumber,
            out sqlState,
            out nativeError,
            out message);
    }

    /// <summary>
    /// Gets a diagnostic field with Unicode string values.
    /// Implements SQLGetDiagFieldW from ODBC 3.8 specification.
    /// </summary>
    /// <param name="handleType">The handle type.</param>
    /// <param name="handle">The handle.</param>
    /// <param name="recordNumber">The record number.</param>
    /// <param name="diagIdentifier">The diagnostic field identifier.</param>
    /// <param name="diagInfo">The diagnostic information.</param>
    /// <returns>SQL_SUCCESS, SQL_NO_DATA, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLGetDiagFieldW(
        SqlHandleType handleType,
        nint handle,
        int recordNumber,
        DiagField diagIdentifier,
        out object? diagInfo)
    {
        return _ansiApi.SQLGetDiagField(
            handleType,
            handle,
            recordNumber,
            diagIdentifier,
            out diagInfo);
    }

    #endregion

    #region Catalog Functions (SQLColumnsW, SQLTablesW)

    /// <summary>
    /// Gets a list of columns with Unicode pattern matching.
    /// Implements SQLColumnsW from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="catalogName">The catalog name pattern in Unicode.</param>
    /// <param name="schemaName">The schema name pattern in Unicode.</param>
    /// <param name="tableName">The table name pattern in Unicode.</param>
    /// <param name="columnName">The column name pattern in Unicode.</param>
    /// <returns>SQL_SUCCESS, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLColumnsW(
        nint statementHandle,
        string? catalogName,
        string? schemaName,
        string? tableName,
        string? columnName)
    {
        return _ansiApi.SQLColumns(statementHandle, catalogName, schemaName, tableName, columnName);
    }

    /// <summary>
    /// Gets a list of tables with Unicode pattern matching.
    /// Implements SQLTablesW from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="catalogName">The catalog name pattern in Unicode.</param>
    /// <param name="schemaName">The schema name pattern in Unicode.</param>
    /// <param name="tableName">The table name pattern in Unicode.</param>
    /// <param name="tableType">The table type list in Unicode.</param>
    /// <returns>SQL_SUCCESS, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLTablesW(
        nint statementHandle,
        string? catalogName,
        string? schemaName,
        string? tableName,
        string? tableType)
    {
        return _ansiApi.SQLTables(statementHandle, catalogName, schemaName, tableName, tableType);
    }

    #endregion

    #region Error Handling (SQLErrorW)

    /// <summary>
    /// Gets error information with Unicode message text.
    /// Implements SQLErrorW from ODBC 3.8 specification.
    /// This is a deprecated function but still supported for compatibility.
    /// </summary>
    /// <param name="environmentHandle">The environment handle.</param>
    /// <param name="connectionHandle">The connection handle.</param>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="sqlState">The SQLSTATE code.</param>
    /// <param name="nativeError">The native error code.</param>
    /// <param name="message">The error message in Unicode.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_NO_DATA, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLErrorW(
        nint environmentHandle,
        nint connectionHandle,
        nint statementHandle,
        out string sqlState,
        out int nativeError,
        out string message)
    {
        sqlState = "";
        nativeError = 0;
        message = "";

        // Try statement first, then connection, then environment
        if (statementHandle != nint.Zero)
        {
            return SQLGetDiagRecW(SqlHandleType.Statement, statementHandle, 1,
                out sqlState, out nativeError, out message);
        }

        if (connectionHandle != nint.Zero)
        {
            return SQLGetDiagRecW(SqlHandleType.Connection, connectionHandle, 1,
                out sqlState, out nativeError, out message);
        }

        if (environmentHandle != nint.Zero)
        {
            return SQLGetDiagRecW(SqlHandleType.Environment, environmentHandle, 1,
                out sqlState, out nativeError, out message);
        }

        return SqlReturn.InvalidHandle;
    }

    #endregion

    #region Native Error Handling (SQLNativeSqlW)

    /// <summary>
    /// Translates a SQL statement to the native SQL of the data source.
    /// Implements SQLNativeSqlW from ODBC 3.8 specification.
    /// </summary>
    /// <param name="connectionHandle">The connection handle.</param>
    /// <param name="inStatementText">The input SQL statement in Unicode.</param>
    /// <param name="outStatementText">The translated SQL statement in Unicode.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLNativeSqlW(
        nint connectionHandle,
        string inStatementText,
        out string outStatementText)
    {
        outStatementText = inStatementText;

        var handleManager = HandleManager.Instance;
        var conn = handleManager.GetConnectionHandle(connectionHandle);
        if (conn == null)
        {
            return SqlReturn.InvalidHandle;
        }

        // DataWarehouse accepts standard SQL, so no translation needed
        // Just return the input as-is
        return SqlReturn.Success;
    }

    #endregion

    #region Data Source Functions (SQLDataSourcesW, SQLDriversW)

    /// <summary>
    /// Lists available data sources.
    /// Implements SQLDataSourcesW from ODBC 3.8 specification.
    /// </summary>
    /// <param name="environmentHandle">The environment handle.</param>
    /// <param name="direction">The enumeration direction.</param>
    /// <param name="serverName">The server name.</param>
    /// <param name="description">The data source description.</param>
    /// <returns>SQL_SUCCESS, SQL_NO_DATA, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLDataSourcesW(
        nint environmentHandle,
        FetchDirection direction,
        out string serverName,
        out string description)
    {
        serverName = "";
        description = "";

        var handleManager = HandleManager.Instance;
        var env = handleManager.GetEnvironmentHandle(environmentHandle);
        if (env == null)
        {
            return SqlReturn.InvalidHandle;
        }

        // Return default DataWarehouse data source
        if (direction == FetchDirection.First || direction == FetchDirection.FirstUser)
        {
            serverName = "DataWarehouse";
            description = "DataWarehouse ODBC Driver";
            return SqlReturn.Success;
        }

        return SqlReturn.NoData;
    }

    /// <summary>
    /// Lists available drivers.
    /// Implements SQLDriversW from ODBC 3.8 specification.
    /// </summary>
    /// <param name="environmentHandle">The environment handle.</param>
    /// <param name="direction">The enumeration direction.</param>
    /// <param name="driverDescription">The driver description.</param>
    /// <param name="driverAttributes">The driver attributes.</param>
    /// <returns>SQL_SUCCESS, SQL_NO_DATA, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLDriversW(
        nint environmentHandle,
        FetchDirection direction,
        out string driverDescription,
        out string driverAttributes)
    {
        driverDescription = "";
        driverAttributes = "";

        var handleManager = HandleManager.Instance;
        var env = handleManager.GetEnvironmentHandle(environmentHandle);
        if (env == null)
        {
            return SqlReturn.InvalidHandle;
        }

        if (direction == FetchDirection.First)
        {
            driverDescription = "DataWarehouse ODBC Driver";
            driverAttributes = "Driver=DataWarehouse";
            return SqlReturn.Success;
        }

        return SqlReturn.NoData;
    }

    #endregion

    #region Browse Connect (SQLBrowseConnectW)

    /// <summary>
    /// Iteratively builds a connection string.
    /// Implements SQLBrowseConnectW from ODBC 3.8 specification.
    /// </summary>
    /// <param name="connectionHandle">The connection handle.</param>
    /// <param name="inConnectionString">The input connection string attributes.</param>
    /// <param name="outConnectionString">The output connection string with required attributes.</param>
    /// <returns>SQL_SUCCESS, SQL_NEED_DATA, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLBrowseConnectW(
        nint connectionHandle,
        string inConnectionString,
        out string outConnectionString)
    {
        outConnectionString = "";

        var handleManager = HandleManager.Instance;
        var conn = handleManager.GetConnectionHandle(connectionHandle);
        if (conn == null)
        {
            return SqlReturn.InvalidHandle;
        }

        // Parse input and determine what's needed
        var hasServer = inConnectionString.Contains("SERVER=", StringComparison.OrdinalIgnoreCase) ||
                        inConnectionString.Contains("DSN=", StringComparison.OrdinalIgnoreCase);
        var hasUser = inConnectionString.Contains("UID=", StringComparison.OrdinalIgnoreCase);
        var hasPwd = inConnectionString.Contains("PWD=", StringComparison.OrdinalIgnoreCase);

        if (!hasServer)
        {
            outConnectionString = "SERVER:Server={?};DSN:DataSourceName={DataWarehouse}";
            return SqlReturn.NeedData;
        }

        if (!hasUser)
        {
            outConnectionString = "UID:UserID={?};PWD:Password={?}";
            return SqlReturn.NeedData;
        }

        // All required attributes present, try to connect
        var result = _ansiApi.SQLDriverConnect(
            connectionHandle,
            nint.Zero,
            inConnectionString,
            out outConnectionString,
            DriverCompletion.NoPrompt);

        return result;
    }

    #endregion
}

/// <summary>
/// Fetch direction for data source/driver enumeration.
/// </summary>
public enum FetchDirection : ushort
{
    /// <summary>
    /// Fetch first item.
    /// </summary>
    First = 2,

    /// <summary>
    /// Fetch next item.
    /// </summary>
    Next = 1,

    /// <summary>
    /// Fetch first user data source.
    /// </summary>
    FirstUser = 31,

    /// <summary>
    /// Fetch first system data source.
    /// </summary>
    FirstSystem = 32
}
