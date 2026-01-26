// <copyright file="OdbcApi.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using DataWarehouse.Plugins.OdbcDriver.Handles;
using System.Text;

namespace DataWarehouse.Plugins.OdbcDriver.Api;

/// <summary>
/// Implements the ODBC 3.8 API functions for the DataWarehouse driver.
/// This class provides the core ODBC functionality including handle management,
/// connection handling, statement execution, and result fetching.
/// Thread-safe: All methods are thread-safe.
/// </summary>
public sealed class OdbcApi
{
    private readonly HandleManager _handleManager;
    private readonly OdbcDriverConfig _config;
    private readonly Func<string, CancellationToken, Task<OdbcQueryResult>>? _sqlExecutor;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="OdbcApi"/> class.
    /// </summary>
    /// <param name="config">The driver configuration.</param>
    /// <param name="sqlExecutor">The SQL executor function.</param>
    public OdbcApi(OdbcDriverConfig config, Func<string, CancellationToken, Task<OdbcQueryResult>>? sqlExecutor = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _handleManager = HandleManager.Instance;
        _sqlExecutor = sqlExecutor;
    }

    #region Handle Allocation (SQLAllocHandle, SQLFreeHandle)

    /// <summary>
    /// Allocates an environment, connection, statement, or descriptor handle.
    /// Implements SQLAllocHandle from ODBC 3.8 specification.
    /// </summary>
    /// <param name="handleType">The type of handle to allocate.</param>
    /// <param name="inputHandle">The parent handle (null for environment handles).</param>
    /// <param name="outputHandle">The allocated handle.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLAllocHandle(SqlHandleType handleType, nint inputHandle, out nint outputHandle)
    {
        outputHandle = nint.Zero;

        try
        {
            return handleType switch
            {
                SqlHandleType.Environment => _handleManager.AllocateEnvironmentHandle(out outputHandle),
                SqlHandleType.Connection => _handleManager.AllocateConnectionHandle(inputHandle, out outputHandle),
                SqlHandleType.Statement => _handleManager.AllocateStatementHandle(inputHandle, out outputHandle),
                SqlHandleType.Descriptor => _handleManager.AllocateDescriptorHandle(inputHandle, out outputHandle),
                _ => SqlReturn.InvalidHandle
            };
        }
        catch
        {
            return SqlReturn.Error;
        }
    }

    /// <summary>
    /// Frees an environment, connection, statement, or descriptor handle.
    /// Implements SQLFreeHandle from ODBC 3.8 specification.
    /// </summary>
    /// <param name="handleType">The type of handle to free.</param>
    /// <param name="handle">The handle to free.</param>
    /// <returns>SQL_SUCCESS, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLFreeHandle(SqlHandleType handleType, nint handle)
    {
        try
        {
            return _handleManager.FreeHandle(handleType, handle);
        }
        catch
        {
            return SqlReturn.InvalidHandle;
        }
    }

    #endregion

    #region Connection Functions (SQLConnect, SQLDriverConnect, SQLDisconnect)

    /// <summary>
    /// Establishes a connection to a data source using DSN, user ID, and password.
    /// Implements SQLConnect from ODBC 3.8 specification.
    /// </summary>
    /// <param name="connectionHandle">The connection handle.</param>
    /// <param name="serverName">The data source name (DSN).</param>
    /// <param name="userName">The user identifier.</param>
    /// <param name="authentication">The authentication string (password).</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLConnect(
        nint connectionHandle,
        string serverName,
        string userName,
        string authentication)
    {
        var conn = _handleManager.GetConnectionHandle(connectionHandle);
        if (conn == null)
        {
            return SqlReturn.InvalidHandle;
        }

        conn.ClearDiagnostics();

        if (string.IsNullOrEmpty(serverName))
        {
            conn.AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.DataSourceNameNotFound,
                Message = "Data source name is required.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        return conn.Connect(serverName, userName, authentication);
    }

    /// <summary>
    /// Establishes a connection using a connection string.
    /// Implements SQLDriverConnect from ODBC 3.8 specification.
    /// </summary>
    /// <param name="connectionHandle">The connection handle.</param>
    /// <param name="windowHandle">The parent window handle (for dialog prompts).</param>
    /// <param name="inConnectionString">The input connection string.</param>
    /// <param name="outConnectionString">The completed connection string.</param>
    /// <param name="driverCompletion">Driver completion option.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_NO_DATA, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLDriverConnect(
        nint connectionHandle,
        nint windowHandle,
        string inConnectionString,
        out string outConnectionString,
        DriverCompletion driverCompletion)
    {
        outConnectionString = string.Empty;

        var conn = _handleManager.GetConnectionHandle(connectionHandle);
        if (conn == null)
        {
            return SqlReturn.InvalidHandle;
        }

        conn.ClearDiagnostics();

        if (string.IsNullOrEmpty(inConnectionString))
        {
            if (driverCompletion == DriverCompletion.NoPrompt)
            {
                conn.AddDiagnostic(new OdbcDiagnosticRecord
                {
                    SqlState = SqlState.NoDataSourceOrDriver,
                    Message = "Connection string is required when driver completion is SQL_DRIVER_NOPROMPT.",
                    NativeError = 0
                });
                return SqlReturn.Error;
            }
        }

        var result = conn.Connect(inConnectionString ?? "");

        if (result == SqlReturn.Success || result == SqlReturn.SuccessWithInfo)
        {
            // Build completed connection string
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(conn.DataSourceName))
                sb.Append($"DSN={conn.DataSourceName};");
            if (!string.IsNullOrEmpty(conn.ServerName))
                sb.Append($"SERVER={conn.ServerName};");
            if (!string.IsNullOrEmpty(conn.DatabaseName))
                sb.Append($"DATABASE={conn.DatabaseName};");
            if (!string.IsNullOrEmpty(conn.UserName))
                sb.Append($"UID={conn.UserName};");

            outConnectionString = sb.ToString();
        }

        return result;
    }

    /// <summary>
    /// Disconnects from a data source.
    /// Implements SQLDisconnect from ODBC 3.8 specification.
    /// </summary>
    /// <param name="connectionHandle">The connection handle.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLDisconnect(nint connectionHandle)
    {
        var conn = _handleManager.GetConnectionHandle(connectionHandle);
        if (conn == null)
        {
            return SqlReturn.InvalidHandle;
        }

        conn.ClearDiagnostics();
        return conn.Disconnect();
    }

    #endregion

    #region Statement Execution (SQLExecDirect, SQLPrepare, SQLExecute)

    /// <summary>
    /// Executes a SQL statement directly.
    /// Implements SQLExecDirect from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="statementText">The SQL statement to execute.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_NEED_DATA, SQL_NO_DATA, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLExecDirect(nint statementHandle, string statementText)
    {
        var stmt = _handleManager.GetStatementHandle(statementHandle);
        if (stmt == null)
        {
            return SqlReturn.InvalidHandle;
        }

        stmt.ClearDiagnostics();

        if (_sqlExecutor == null)
        {
            stmt.AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.GeneralError,
                Message = "SQL executor is not configured.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        // Execute synchronously for ODBC compatibility
        var task = stmt.ExecDirectAsync(statementText, _sqlExecutor, CancellationToken.None);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Prepares a SQL statement for execution.
    /// Implements SQLPrepare from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="statementText">The SQL statement to prepare.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLPrepare(nint statementHandle, string statementText)
    {
        var stmt = _handleManager.GetStatementHandle(statementHandle);
        if (stmt == null)
        {
            return SqlReturn.InvalidHandle;
        }

        stmt.ClearDiagnostics();
        return stmt.Prepare(statementText);
    }

    /// <summary>
    /// Executes a prepared statement.
    /// Implements SQLExecute from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_NEED_DATA, SQL_NO_DATA, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLExecute(nint statementHandle)
    {
        var stmt = _handleManager.GetStatementHandle(statementHandle);
        if (stmt == null)
        {
            return SqlReturn.InvalidHandle;
        }

        stmt.ClearDiagnostics();

        if (_sqlExecutor == null)
        {
            stmt.AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.GeneralError,
                Message = "SQL executor is not configured.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        var task = stmt.ExecuteAsync(_sqlExecutor, CancellationToken.None);
        return task.GetAwaiter().GetResult();
    }

    #endregion

    #region Result Fetching (SQLFetch, SQLGetData)

    /// <summary>
    /// Fetches the next row from a result set.
    /// Implements SQLFetch from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_NO_DATA, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLFetch(nint statementHandle)
    {
        var stmt = _handleManager.GetStatementHandle(statementHandle);
        if (stmt == null)
        {
            return SqlReturn.InvalidHandle;
        }

        return stmt.Fetch();
    }

    /// <summary>
    /// Gets data from a column in the current row.
    /// Implements SQLGetData from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="columnNumber">The 1-based column number.</param>
    /// <param name="targetType">The target C data type.</param>
    /// <param name="targetValue">The retrieved value.</param>
    /// <param name="indicator">The length indicator or SQL_NULL_DATA.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_NO_DATA, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLGetData(
        nint statementHandle,
        int columnNumber,
        SqlCType targetType,
        out object? targetValue,
        out nint indicator)
    {
        targetValue = null;
        indicator = -1;

        var stmt = _handleManager.GetStatementHandle(statementHandle);
        if (stmt == null)
        {
            return SqlReturn.InvalidHandle;
        }

        return stmt.GetData(columnNumber, targetType, out targetValue, out indicator);
    }

    #endregion

    #region Column Metadata (SQLDescribeCol, SQLNumResultCols, SQLRowCount)

    /// <summary>
    /// Describes a column in the result set.
    /// Implements SQLDescribeCol from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="columnNumber">The 1-based column number.</param>
    /// <param name="columnName">The column name.</param>
    /// <param name="dataType">The SQL data type.</param>
    /// <param name="columnSize">The column size.</param>
    /// <param name="decimalDigits">The decimal digits (scale).</param>
    /// <param name="nullable">Whether the column allows NULL values.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLDescribeCol(
        nint statementHandle,
        int columnNumber,
        out string columnName,
        out SqlDataType dataType,
        out int columnSize,
        out short decimalDigits,
        out SqlNullable nullable)
    {
        columnName = "";
        dataType = SqlDataType.Unknown;
        columnSize = 0;
        decimalDigits = 0;
        nullable = SqlNullable.Unknown;

        var stmt = _handleManager.GetStatementHandle(statementHandle);
        if (stmt == null)
        {
            return SqlReturn.InvalidHandle;
        }

        var result = stmt.DescribeCol(columnNumber, out var colInfo);

        if (result == SqlReturn.Success && colInfo != null)
        {
            columnName = colInfo.Name;
            dataType = colInfo.SqlType;
            columnSize = colInfo.ColumnSize;
            decimalDigits = colInfo.DecimalDigits;
            nullable = colInfo.IsNullable ? SqlNullable.Nullable : SqlNullable.NoNulls;
        }

        return result;
    }

    /// <summary>
    /// Gets the number of columns in a result set.
    /// Implements SQLNumResultCols from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="columnCount">The number of columns.</param>
    /// <returns>SQL_SUCCESS, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLNumResultCols(nint statementHandle, out int columnCount)
    {
        columnCount = 0;

        var stmt = _handleManager.GetStatementHandle(statementHandle);
        if (stmt == null)
        {
            return SqlReturn.InvalidHandle;
        }

        return stmt.NumResultCols(out columnCount);
    }

    /// <summary>
    /// Gets the number of rows affected by an UPDATE, INSERT, or DELETE statement.
    /// Implements SQLRowCount from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="rowCount">The row count.</param>
    /// <returns>SQL_SUCCESS, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLRowCount(nint statementHandle, out long rowCount)
    {
        rowCount = -1;

        var stmt = _handleManager.GetStatementHandle(statementHandle);
        if (stmt == null)
        {
            return SqlReturn.InvalidHandle;
        }

        return stmt.RowCountResult(out rowCount);
    }

    #endregion

    #region Parameter and Column Binding (SQLBindParameter, SQLBindCol)

    /// <summary>
    /// Binds a parameter to a prepared statement.
    /// Implements SQLBindParameter from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="parameterNumber">The 1-based parameter number.</param>
    /// <param name="inputOutputType">The parameter direction.</param>
    /// <param name="valueType">The C data type of the parameter.</param>
    /// <param name="parameterType">The SQL data type of the parameter.</param>
    /// <param name="columnSize">The column size.</param>
    /// <param name="decimalDigits">The decimal digits.</param>
    /// <param name="parameterValue">The parameter value.</param>
    /// <param name="bufferLength">The buffer length.</param>
    /// <param name="strLenOrIndPtr">The length/indicator pointer.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLBindParameter(
        nint statementHandle,
        int parameterNumber,
        ParameterDirection inputOutputType,
        SqlCType valueType,
        SqlDataType parameterType,
        int columnSize,
        short decimalDigits,
        object? parameterValue,
        int bufferLength,
        nint strLenOrIndPtr)
    {
        var stmt = _handleManager.GetStatementHandle(statementHandle);
        if (stmt == null)
        {
            return SqlReturn.InvalidHandle;
        }

        var boundParam = new OdbcBoundParameter
        {
            ParameterNumber = parameterNumber,
            Direction = inputOutputType,
            ValueType = valueType,
            ParameterType = parameterType,
            ColumnSize = columnSize,
            DecimalDigits = decimalDigits,
            Value = parameterValue,
            BufferLength = bufferLength,
            Indicator = strLenOrIndPtr
        };

        return stmt.BindParameter(parameterNumber, boundParam);
    }

    /// <summary>
    /// Binds a column to a data buffer.
    /// Implements SQLBindCol from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="columnNumber">The 1-based column number.</param>
    /// <param name="targetType">The C data type.</param>
    /// <param name="bufferLength">The buffer length.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLBindCol(
        nint statementHandle,
        int columnNumber,
        SqlCType targetType,
        int bufferLength)
    {
        var stmt = _handleManager.GetStatementHandle(statementHandle);
        if (stmt == null)
        {
            return SqlReturn.InvalidHandle;
        }

        var boundCol = new OdbcBoundColumn
        {
            ColumnNumber = columnNumber,
            TargetType = targetType,
            BufferLength = bufferLength
        };

        return stmt.BindColumn(columnNumber, boundCol);
    }

    #endregion

    #region Statement Options (SQLFreeStmt, SQLCloseCursor)

    /// <summary>
    /// Frees statement resources.
    /// Implements SQLFreeStmt from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="option">The free option.</param>
    /// <returns>SQL_SUCCESS, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLFreeStmt(nint statementHandle, FreeStmtOption option)
    {
        var stmt = _handleManager.GetStatementHandle(statementHandle);
        if (stmt == null)
        {
            return SqlReturn.InvalidHandle;
        }

        return stmt.FreeStmt(option);
    }

    /// <summary>
    /// Closes a cursor on a statement.
    /// Implements SQLCloseCursor from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <returns>SQL_SUCCESS, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLCloseCursor(nint statementHandle)
    {
        return SQLFreeStmt(statementHandle, FreeStmtOption.Close);
    }

    #endregion

    #region Attribute Functions

    /// <summary>
    /// Sets an environment attribute.
    /// Implements SQLSetEnvAttr from ODBC 3.8 specification.
    /// </summary>
    /// <param name="environmentHandle">The environment handle.</param>
    /// <param name="attribute">The attribute to set.</param>
    /// <param name="value">The attribute value.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLSetEnvAttr(nint environmentHandle, SqlEnvAttribute attribute, nint value)
    {
        var env = _handleManager.GetEnvironmentHandle(environmentHandle);
        if (env == null)
        {
            return SqlReturn.InvalidHandle;
        }

        env.ClearDiagnostics();
        return env.SetAttribute(attribute, value);
    }

    /// <summary>
    /// Gets an environment attribute.
    /// Implements SQLGetEnvAttr from ODBC 3.8 specification.
    /// </summary>
    /// <param name="environmentHandle">The environment handle.</param>
    /// <param name="attribute">The attribute to get.</param>
    /// <param name="value">The attribute value.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLGetEnvAttr(nint environmentHandle, SqlEnvAttribute attribute, out nint value)
    {
        value = nint.Zero;

        var env = _handleManager.GetEnvironmentHandle(environmentHandle);
        if (env == null)
        {
            return SqlReturn.InvalidHandle;
        }

        return env.GetAttribute(attribute, out value);
    }

    /// <summary>
    /// Sets a connection attribute.
    /// Implements SQLSetConnectAttr from ODBC 3.8 specification.
    /// </summary>
    /// <param name="connectionHandle">The connection handle.</param>
    /// <param name="attribute">The attribute to set.</param>
    /// <param name="value">The attribute value.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLSetConnectAttr(nint connectionHandle, SqlConnectAttribute attribute, nint value)
    {
        var conn = _handleManager.GetConnectionHandle(connectionHandle);
        if (conn == null)
        {
            return SqlReturn.InvalidHandle;
        }

        conn.ClearDiagnostics();
        return conn.SetAttribute(attribute, value);
    }

    /// <summary>
    /// Gets a connection attribute.
    /// Implements SQLGetConnectAttr from ODBC 3.8 specification.
    /// </summary>
    /// <param name="connectionHandle">The connection handle.</param>
    /// <param name="attribute">The attribute to get.</param>
    /// <param name="value">The attribute value.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLGetConnectAttr(nint connectionHandle, SqlConnectAttribute attribute, out nint value)
    {
        value = nint.Zero;

        var conn = _handleManager.GetConnectionHandle(connectionHandle);
        if (conn == null)
        {
            return SqlReturn.InvalidHandle;
        }

        return conn.GetAttribute(attribute, out value);
    }

    /// <summary>
    /// Sets a statement attribute.
    /// Implements SQLSetStmtAttr from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="attribute">The attribute to set.</param>
    /// <param name="value">The attribute value.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLSetStmtAttr(nint statementHandle, SqlStmtAttribute attribute, nint value)
    {
        var stmt = _handleManager.GetStatementHandle(statementHandle);
        if (stmt == null)
        {
            return SqlReturn.InvalidHandle;
        }

        stmt.ClearDiagnostics();
        return stmt.SetAttribute(attribute, value);
    }

    /// <summary>
    /// Gets a statement attribute.
    /// Implements SQLGetStmtAttr from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="attribute">The attribute to get.</param>
    /// <param name="value">The attribute value.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLGetStmtAttr(nint statementHandle, SqlStmtAttribute attribute, out nint value)
    {
        value = nint.Zero;

        var stmt = _handleManager.GetStatementHandle(statementHandle);
        if (stmt == null)
        {
            return SqlReturn.InvalidHandle;
        }

        return stmt.GetAttribute(attribute, out value);
    }

    #endregion

    #region Information Functions (SQLGetInfo, SQLGetDiagRec)

    /// <summary>
    /// Gets information about the driver and data source.
    /// Implements SQLGetInfo from ODBC 3.8 specification.
    /// </summary>
    /// <param name="connectionHandle">The connection handle.</param>
    /// <param name="infoType">The type of information to retrieve.</param>
    /// <param name="infoValue">The information value.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLGetInfo(nint connectionHandle, SqlInfoType infoType, out object? infoValue)
    {
        infoValue = null;

        var conn = _handleManager.GetConnectionHandle(connectionHandle);
        if (conn == null)
        {
            return SqlReturn.InvalidHandle;
        }

        infoValue = infoType switch
        {
            SqlInfoType.DriverName => _config.DriverName,
            SqlInfoType.DriverVer => _config.DriverVersion,
            SqlInfoType.DriverOdbcVer => _config.OdbcVersion,
            SqlInfoType.DataSourceName => conn.DataSourceName ?? "",
            SqlInfoType.ServerName => conn.ServerName ?? "localhost",
            SqlInfoType.DatabaseName => conn.DatabaseName ?? _config.DefaultDatabase,
            SqlInfoType.DbmsName => "DataWarehouse",
            SqlInfoType.DbmsVer => "1.0.0",
            SqlInfoType.UserName => conn.UserName ?? "",
            SqlInfoType.MaxCatalogNameLen => 128,
            SqlInfoType.MaxColumnNameLen => 128,
            SqlInfoType.MaxCursorNameLen => 128,
            SqlInfoType.MaxSchemaNameLen => 128,
            SqlInfoType.MaxTableNameLen => 128,
            SqlInfoType.MaxUserNameLen => 128,
            SqlInfoType.MaxIdentifierLen => 128,
            SqlInfoType.IdentifierQuoteChar => "\"",
            SqlInfoType.SearchPatternEscape => "\\",
            SqlInfoType.CatalogTerm => "database",
            SqlInfoType.SchemaTerm => "schema",
            SqlInfoType.TableTerm => "table",
            SqlInfoType.ProcedureTerm => "procedure",
            SqlInfoType.CatalogNameSeparator => ".",
            SqlInfoType.TxnCapable => 2, // SQL_TC_DML
            SqlInfoType.DefaultTxnIsolation => (int)TransactionIsolationLevel.ReadCommitted,
            SqlInfoType.TxnIsolationOption => 15, // All levels
            SqlInfoType.MultResultSets => "Y",
            SqlInfoType.DataSourceReadOnly => "N",
            SqlInfoType.AccessibleTables => "Y",
            SqlInfoType.AccessibleProcedures => "N",
            SqlInfoType.MaxStatementLen => 1048576,
            SqlInfoType.ActiveConnections => _handleManager.GetHandleCount(SqlHandleType.Connection),
            SqlInfoType.ActiveStatements => _handleManager.GetHandleCount(SqlHandleType.Statement),
            SqlInfoType.SqlConformance => 1, // SQL-92 Entry Level
            SqlInfoType.OdbcInterfaceConformance => 1, // Core
            SqlInfoType.ScrollOptions => 1, // Forward only
            SqlInfoType.CursorCommitBehavior => 1, // Close cursors
            SqlInfoType.CursorRollbackBehavior => 1, // Close cursors
            SqlInfoType.GetdataExtensions => 1, // Any column, any order
            SqlInfoType.NullCollation => 1, // High
            SqlInfoType.Keywords => "BLOB,MANIFEST,METADATA",
            SqlInfoType.SpecialCharacters => "",
            _ => null
        };

        if (infoValue == null)
        {
            conn.AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.InvalidInfoType,
                Message = $"Unknown info type: {infoType}",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        return SqlReturn.Success;
    }

    /// <summary>
    /// Gets diagnostic information (error/warning records).
    /// Implements SQLGetDiagRec from ODBC 3.8 specification.
    /// </summary>
    /// <param name="handleType">The handle type.</param>
    /// <param name="handle">The handle.</param>
    /// <param name="recordNumber">The 1-based record number.</param>
    /// <param name="sqlState">The SQLSTATE code.</param>
    /// <param name="nativeError">The native error code.</param>
    /// <param name="message">The error message.</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_NO_DATA, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLGetDiagRec(
        SqlHandleType handleType,
        nint handle,
        int recordNumber,
        out string sqlState,
        out int nativeError,
        out string message)
    {
        sqlState = "";
        nativeError = 0;
        message = "";

        OdbcDiagnosticRecord? record = null;

        switch (handleType)
        {
            case SqlHandleType.Environment:
                var env = _handleManager.GetEnvironmentHandle(handle);
                if (env == null) return SqlReturn.InvalidHandle;
                record = env.GetDiagnostic(recordNumber);
                break;

            case SqlHandleType.Connection:
                var conn = _handleManager.GetConnectionHandle(handle);
                if (conn == null) return SqlReturn.InvalidHandle;
                record = conn.GetDiagnostic(recordNumber);
                break;

            case SqlHandleType.Statement:
                var stmt = _handleManager.GetStatementHandle(handle);
                if (stmt == null) return SqlReturn.InvalidHandle;
                record = stmt.GetDiagnostic(recordNumber);
                break;

            case SqlHandleType.Descriptor:
                var desc = _handleManager.GetDescriptorHandle(handle);
                if (desc == null) return SqlReturn.InvalidHandle;
                record = desc.GetDiagnostic(recordNumber);
                break;

            default:
                return SqlReturn.InvalidHandle;
        }

        if (record == null)
        {
            return SqlReturn.NoData;
        }

        sqlState = record.SqlState;
        nativeError = record.NativeError;
        message = record.Message;

        return SqlReturn.Success;
    }

    /// <summary>
    /// Gets a diagnostic field.
    /// Implements SQLGetDiagField from ODBC 3.8 specification.
    /// </summary>
    /// <param name="handleType">The handle type.</param>
    /// <param name="handle">The handle.</param>
    /// <param name="recordNumber">The record number (0 for header fields).</param>
    /// <param name="diagIdentifier">The diagnostic field identifier.</param>
    /// <param name="diagInfo">The diagnostic information.</param>
    /// <returns>SQL_SUCCESS, SQL_NO_DATA, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLGetDiagField(
        SqlHandleType handleType,
        nint handle,
        int recordNumber,
        DiagField diagIdentifier,
        out object? diagInfo)
    {
        diagInfo = null;

        // Get diagnostics based on handle type
        IEnumerable<OdbcDiagnosticRecord>? diagnostics = null;

        switch (handleType)
        {
            case SqlHandleType.Environment:
                var env = _handleManager.GetEnvironmentHandle(handle);
                if (env == null) return SqlReturn.InvalidHandle;
                diagnostics = env.GetDiagnostics();
                break;

            case SqlHandleType.Connection:
                var conn = _handleManager.GetConnectionHandle(handle);
                if (conn == null) return SqlReturn.InvalidHandle;
                diagnostics = conn.GetDiagnostics();
                break;

            case SqlHandleType.Statement:
                var stmt = _handleManager.GetStatementHandle(handle);
                if (stmt == null) return SqlReturn.InvalidHandle;
                diagnostics = stmt.GetDiagnostics();
                break;

            case SqlHandleType.Descriptor:
                var desc = _handleManager.GetDescriptorHandle(handle);
                if (desc == null) return SqlReturn.InvalidHandle;
                diagnostics = desc.GetDiagnostics();
                break;

            default:
                return SqlReturn.InvalidHandle;
        }

        var diagArray = diagnostics.ToArray();

        // Header fields (record 0)
        if (recordNumber == 0)
        {
            diagInfo = diagIdentifier switch
            {
                DiagField.Number => diagArray.Length,
                DiagField.ReturnCode => SqlReturn.Success,
                DiagField.CursorRowCount => 0L,
                DiagField.DynamicFunction => "",
                DiagField.DynamicFunctionCode => 0,
                DiagField.RowCount => 0L,
                _ => null
            };
            return diagInfo != null ? SqlReturn.Success : SqlReturn.Error;
        }

        // Record fields
        if (recordNumber < 1 || recordNumber > diagArray.Length)
        {
            return SqlReturn.NoData;
        }

        var record = diagArray[recordNumber - 1];

        diagInfo = diagIdentifier switch
        {
            DiagField.ClassOrigin => "ISO 9075",
            DiagField.ColumnNumber => record.ColumnNumber,
            DiagField.ConnectionName => record.ConnectionName ?? "",
            DiagField.MessageText => record.Message,
            DiagField.NativeError => record.NativeError,
            DiagField.RowNumber => record.RowNumber,
            DiagField.ServerName => record.ServerName ?? "",
            DiagField.Sqlstate => record.SqlState,
            DiagField.SubclassOrigin => "ODBC 3.0",
            _ => null
        };

        return diagInfo != null ? SqlReturn.Success : SqlReturn.Error;
    }

    #endregion

    #region Transaction Functions (SQLEndTran)

    /// <summary>
    /// Commits or rolls back a transaction.
    /// Implements SQLEndTran from ODBC 3.8 specification.
    /// </summary>
    /// <param name="handleType">The handle type (environment or connection).</param>
    /// <param name="handle">The handle.</param>
    /// <param name="completionType">The completion type (commit or rollback).</param>
    /// <returns>SQL_SUCCESS, SQL_SUCCESS_WITH_INFO, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLEndTran(SqlHandleType handleType, nint handle, TransactionCompletionType completionType)
    {
        if (handleType == SqlHandleType.Connection)
        {
            var conn = _handleManager.GetConnectionHandle(handle);
            if (conn == null)
            {
                return SqlReturn.InvalidHandle;
            }

            // For DataWarehouse, transactions are typically auto-committed
            // This is a no-op but we accept the call
            return SqlReturn.Success;
        }

        if (handleType == SqlHandleType.Environment)
        {
            var env = _handleManager.GetEnvironmentHandle(handle);
            if (env == null)
            {
                return SqlReturn.InvalidHandle;
            }

            // Commit/rollback all connections in this environment
            foreach (var conn in env.Connections)
            {
                // No-op for auto-commit connections
            }

            return SqlReturn.Success;
        }

        return SqlReturn.InvalidHandle;
    }

    #endregion

    #region Catalog Functions

    /// <summary>
    /// Gets a list of columns in specified tables.
    /// Implements SQLColumns from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="catalogName">The catalog name pattern.</param>
    /// <param name="schemaName">The schema name pattern.</param>
    /// <param name="tableName">The table name pattern.</param>
    /// <param name="columnName">The column name pattern.</param>
    /// <returns>SQL_SUCCESS, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLColumns(
        nint statementHandle,
        string? catalogName,
        string? schemaName,
        string? tableName,
        string? columnName)
    {
        var stmt = _handleManager.GetStatementHandle(statementHandle);
        if (stmt == null)
        {
            return SqlReturn.InvalidHandle;
        }

        // Return empty result set with proper column structure for catalog queries
        var result = new OdbcQueryResult
        {
            HasResultSet = true,
            Columns = new List<OdbcColumnInfo>
            {
                new() { Name = "TABLE_CAT", Ordinal = 1, SqlType = SqlDataType.VarChar, ColumnSize = 128 },
                new() { Name = "TABLE_SCHEM", Ordinal = 2, SqlType = SqlDataType.VarChar, ColumnSize = 128 },
                new() { Name = "TABLE_NAME", Ordinal = 3, SqlType = SqlDataType.VarChar, ColumnSize = 128 },
                new() { Name = "COLUMN_NAME", Ordinal = 4, SqlType = SqlDataType.VarChar, ColumnSize = 128 },
                new() { Name = "DATA_TYPE", Ordinal = 5, SqlType = SqlDataType.SmallInt, ColumnSize = 5 },
                new() { Name = "TYPE_NAME", Ordinal = 6, SqlType = SqlDataType.VarChar, ColumnSize = 128 },
                new() { Name = "COLUMN_SIZE", Ordinal = 7, SqlType = SqlDataType.Integer, ColumnSize = 10 },
                new() { Name = "BUFFER_LENGTH", Ordinal = 8, SqlType = SqlDataType.Integer, ColumnSize = 10 },
                new() { Name = "DECIMAL_DIGITS", Ordinal = 9, SqlType = SqlDataType.SmallInt, ColumnSize = 5 },
                new() { Name = "NUM_PREC_RADIX", Ordinal = 10, SqlType = SqlDataType.SmallInt, ColumnSize = 5 },
                new() { Name = "NULLABLE", Ordinal = 11, SqlType = SqlDataType.SmallInt, ColumnSize = 5 },
                new() { Name = "REMARKS", Ordinal = 12, SqlType = SqlDataType.VarChar, ColumnSize = 254 },
                new() { Name = "COLUMN_DEF", Ordinal = 13, SqlType = SqlDataType.VarChar, ColumnSize = 254 },
                new() { Name = "SQL_DATA_TYPE", Ordinal = 14, SqlType = SqlDataType.SmallInt, ColumnSize = 5 },
                new() { Name = "SQL_DATETIME_SUB", Ordinal = 15, SqlType = SqlDataType.SmallInt, ColumnSize = 5 },
                new() { Name = "CHAR_OCTET_LENGTH", Ordinal = 16, SqlType = SqlDataType.Integer, ColumnSize = 10 },
                new() { Name = "ORDINAL_POSITION", Ordinal = 17, SqlType = SqlDataType.Integer, ColumnSize = 10 },
                new() { Name = "IS_NULLABLE", Ordinal = 18, SqlType = SqlDataType.VarChar, ColumnSize = 254 }
            },
            Rows = new List<object?[]>()
        };

        // In a real implementation, query the metadata and populate rows
        // For now, return empty result

        var field = typeof(OdbcStatementHandle).GetProperty("CurrentResult",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Use reflection to set internal state or implement a proper method
        stmt.GetType().GetProperty("CurrentResult")?.SetValue(stmt, result);

        return SqlReturn.Success;
    }

    /// <summary>
    /// Gets a list of tables in the data source.
    /// Implements SQLTables from ODBC 3.8 specification.
    /// </summary>
    /// <param name="statementHandle">The statement handle.</param>
    /// <param name="catalogName">The catalog name pattern.</param>
    /// <param name="schemaName">The schema name pattern.</param>
    /// <param name="tableName">The table name pattern.</param>
    /// <param name="tableType">The table type list.</param>
    /// <returns>SQL_SUCCESS, SQL_INVALID_HANDLE, or SQL_ERROR.</returns>
    public SqlReturn SQLTables(
        nint statementHandle,
        string? catalogName,
        string? schemaName,
        string? tableName,
        string? tableType)
    {
        var stmt = _handleManager.GetStatementHandle(statementHandle);
        if (stmt == null)
        {
            return SqlReturn.InvalidHandle;
        }

        // Return empty result set with proper column structure
        var result = new OdbcQueryResult
        {
            HasResultSet = true,
            Columns = new List<OdbcColumnInfo>
            {
                new() { Name = "TABLE_CAT", Ordinal = 1, SqlType = SqlDataType.VarChar, ColumnSize = 128 },
                new() { Name = "TABLE_SCHEM", Ordinal = 2, SqlType = SqlDataType.VarChar, ColumnSize = 128 },
                new() { Name = "TABLE_NAME", Ordinal = 3, SqlType = SqlDataType.VarChar, ColumnSize = 128 },
                new() { Name = "TABLE_TYPE", Ordinal = 4, SqlType = SqlDataType.VarChar, ColumnSize = 128 },
                new() { Name = "REMARKS", Ordinal = 5, SqlType = SqlDataType.VarChar, ColumnSize = 254 }
            },
            Rows = new List<object?[]>
            {
                new object?[] { _config.DefaultDatabase, _config.DefaultSchema, "manifests", "TABLE", "Blob manifests" },
                new object?[] { _config.DefaultDatabase, _config.DefaultSchema, "blobs", "TABLE", "Blob storage" }
            }
        };

        return SqlReturn.Success;
    }

    #endregion
}

/// <summary>
/// Driver completion options for SQLDriverConnect.
/// </summary>
public enum DriverCompletion
{
    /// <summary>
    /// Do not prompt the user.
    /// </summary>
    NoPrompt = 0,

    /// <summary>
    /// Prompt if necessary.
    /// </summary>
    Complete = 1,

    /// <summary>
    /// Always prompt.
    /// </summary>
    Prompt = 2,

    /// <summary>
    /// Prompt with required fields only.
    /// </summary>
    CompleteRequired = 3
}

/// <summary>
/// Transaction completion types for SQLEndTran.
/// </summary>
public enum TransactionCompletionType
{
    /// <summary>
    /// Commit the transaction.
    /// </summary>
    Commit = 0,

    /// <summary>
    /// Rollback the transaction.
    /// </summary>
    Rollback = 1
}

/// <summary>
/// Diagnostic field identifiers for SQLGetDiagField.
/// </summary>
public enum DiagField
{
    /// <summary>
    /// Cursor row count.
    /// </summary>
    CursorRowCount = -1249,

    /// <summary>
    /// Dynamic function.
    /// </summary>
    DynamicFunction = 7,

    /// <summary>
    /// Dynamic function code.
    /// </summary>
    DynamicFunctionCode = 12,

    /// <summary>
    /// Number of diagnostic records.
    /// </summary>
    Number = 2,

    /// <summary>
    /// Return code.
    /// </summary>
    ReturnCode = 1,

    /// <summary>
    /// Row count.
    /// </summary>
    RowCount = 3,

    /// <summary>
    /// Class origin.
    /// </summary>
    ClassOrigin = 8,

    /// <summary>
    /// Column number.
    /// </summary>
    ColumnNumber = -1247,

    /// <summary>
    /// Connection name.
    /// </summary>
    ConnectionName = 10,

    /// <summary>
    /// Message text.
    /// </summary>
    MessageText = 6,

    /// <summary>
    /// Native error code.
    /// </summary>
    NativeError = 5,

    /// <summary>
    /// Row number.
    /// </summary>
    RowNumber = -1248,

    /// <summary>
    /// Server name.
    /// </summary>
    ServerName = 11,

    /// <summary>
    /// SQLSTATE.
    /// </summary>
    Sqlstate = 4,

    /// <summary>
    /// Subclass origin.
    /// </summary>
    SubclassOrigin = 9
}
