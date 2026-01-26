// <copyright file="OdbcStatementHandle.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.OdbcDriver.Handles;

/// <summary>
/// Represents an ODBC statement handle (HSTMT).
/// The statement handle manages SQL statement execution and result processing.
/// </summary>
public sealed class OdbcStatementHandle : IDisposable
{
    private readonly ConcurrentQueue<OdbcDiagnosticRecord> _diagnostics = new();
    private readonly Dictionary<int, OdbcBoundParameter> _boundParameters = new();
    private readonly Dictionary<int, OdbcBoundColumn> _boundColumns = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="OdbcStatementHandle"/> class.
    /// </summary>
    /// <param name="handle">The native handle value.</param>
    /// <param name="connection">The parent connection handle.</param>
    public OdbcStatementHandle(nint handle, OdbcConnectionHandle connection)
    {
        Handle = handle;
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets the native handle value.
    /// </summary>
    public nint Handle { get; }

    /// <summary>
    /// Gets the parent connection handle.
    /// </summary>
    public OdbcConnectionHandle Connection { get; }

    /// <summary>
    /// Gets the timestamp when this handle was created.
    /// </summary>
    public DateTime CreatedAt { get; }

    /// <summary>
    /// Gets or sets the current state of the statement.
    /// </summary>
    public StatementState State { get; set; } = StatementState.Allocated;

    /// <summary>
    /// Gets or sets the SQL text of the statement.
    /// </summary>
    public string? SqlText { get; set; }

    /// <summary>
    /// Gets or sets the prepared SQL text (for prepared statements).
    /// </summary>
    public string? PreparedSqlText { get; set; }

    /// <summary>
    /// Gets or sets whether the statement is prepared.
    /// </summary>
    public bool IsPrepared { get; set; }

    /// <summary>
    /// Gets or sets the query timeout in seconds.
    /// </summary>
    public int QueryTimeout { get; set; } = 300;

    /// <summary>
    /// Gets or sets the maximum number of rows to return.
    /// </summary>
    public long MaxRows { get; set; }

    /// <summary>
    /// Gets or sets the cursor type.
    /// </summary>
    public CursorType CursorType { get; set; } = CursorType.ForwardOnly;

    /// <summary>
    /// Gets or sets the concurrency type.
    /// </summary>
    public ConcurrencyType Concurrency { get; set; } = ConcurrencyType.ReadOnly;

    /// <summary>
    /// Gets or sets whether to use bookmarks.
    /// </summary>
    public bool UseBookmarks { get; set; }

    /// <summary>
    /// Gets or sets the row array size for bulk fetching.
    /// </summary>
    public int RowArraySize { get; set; } = 1;

    /// <summary>
    /// Gets or sets the row bind type.
    /// </summary>
    public int RowBindType { get; set; }

    /// <summary>
    /// Gets or sets the parameter set size.
    /// </summary>
    public int ParamsetSize { get; set; } = 1;

    /// <summary>
    /// Gets or sets whether async operations are enabled.
    /// </summary>
    public bool AsyncEnabled { get; set; }

    /// <summary>
    /// Gets the current query result.
    /// </summary>
    public OdbcQueryResult? CurrentResult { get; private set; }

    /// <summary>
    /// Gets or sets the current row position (0-based, -1 before first).
    /// </summary>
    public long CurrentRowIndex { get; set; } = -1;

    /// <summary>
    /// Gets the column metadata for the current result set.
    /// </summary>
    public List<OdbcColumnInfo> Columns => CurrentResult?.Columns ?? new List<OdbcColumnInfo>();

    /// <summary>
    /// Gets the number of columns in the current result set.
    /// </summary>
    public int ColumnCount => CurrentResult?.Columns.Count ?? 0;

    /// <summary>
    /// Gets the number of rows affected by the last statement.
    /// </summary>
    public long RowCount => CurrentResult?.RowsAffected ?? -1;

    /// <summary>
    /// Gets the bound parameters.
    /// </summary>
    public IReadOnlyDictionary<int, OdbcBoundParameter> BoundParameters => _boundParameters;

    /// <summary>
    /// Gets the bound columns.
    /// </summary>
    public IReadOnlyDictionary<int, OdbcBoundColumn> BoundColumns => _boundColumns;

    /// <summary>
    /// Prepares an SQL statement for execution.
    /// </summary>
    /// <param name="sql">The SQL statement to prepare.</param>
    /// <returns>SQL_SUCCESS on success, SQL_ERROR on failure.</returns>
    public SqlReturn Prepare(string sql)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(sql))
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.SyntaxErrorOrAccessViolation,
                Message = "SQL statement cannot be empty.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        PreparedSqlText = sql;
        IsPrepared = true;
        State = StatementState.Prepared;
        CurrentRowIndex = -1;

        return SqlReturn.Success;
    }

    /// <summary>
    /// Executes the prepared statement.
    /// </summary>
    /// <param name="executor">The SQL executor function.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>SQL_SUCCESS on success, SQL_ERROR on failure.</returns>
    public async Task<SqlReturn> ExecuteAsync(
        Func<string, CancellationToken, Task<OdbcQueryResult>> executor,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (!IsPrepared || string.IsNullOrEmpty(PreparedSqlText))
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.FunctionSequenceError,
                Message = "Statement is not prepared.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        return await ExecuteInternalAsync(PreparedSqlText, executor, ct);
    }

    /// <summary>
    /// Executes an SQL statement directly.
    /// </summary>
    /// <param name="sql">The SQL statement to execute.</param>
    /// <param name="executor">The SQL executor function.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>SQL_SUCCESS on success, SQL_ERROR on failure.</returns>
    public async Task<SqlReturn> ExecDirectAsync(
        string sql,
        Func<string, CancellationToken, Task<OdbcQueryResult>> executor,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(sql))
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.SyntaxErrorOrAccessViolation,
                Message = "SQL statement cannot be empty.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        SqlText = sql;
        return await ExecuteInternalAsync(sql, executor, ct);
    }

    private async Task<SqlReturn> ExecuteInternalAsync(
        string sql,
        Func<string, CancellationToken, Task<OdbcQueryResult>> executor,
        CancellationToken ct)
    {
        try
        {
            // Substitute bound parameters
            var processedSql = SubstituteParameters(sql);

            State = StatementState.Executed;
            CurrentResult = await executor(processedSql, ct);
            CurrentRowIndex = -1;

            if (CurrentResult.ErrorMessage != null)
            {
                AddDiagnostic(new OdbcDiagnosticRecord
                {
                    SqlState = CurrentResult.ErrorSqlState ?? SqlState.GeneralError,
                    Message = CurrentResult.ErrorMessage,
                    NativeError = 0
                });
                State = StatementState.Prepared;
                return SqlReturn.Error;
            }

            if (CurrentResult.HasResultSet)
            {
                State = StatementState.ResultSet;
            }

            return SqlReturn.Success;
        }
        catch (OperationCanceledException)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.OperationCanceled,
                Message = "Query execution was canceled.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }
        catch (Exception ex)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.GeneralError,
                Message = $"Query execution failed: {ex.Message}",
                NativeError = 0
            });
            return SqlReturn.Error;
        }
    }

    /// <summary>
    /// Fetches the next row from the result set.
    /// </summary>
    /// <returns>SQL_SUCCESS if a row was fetched, SQL_NO_DATA if no more rows.</returns>
    public SqlReturn Fetch()
    {
        ThrowIfDisposed();

        if (CurrentResult == null || !CurrentResult.HasResultSet)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.InvalidCursorState,
                Message = "No result set is available.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        CurrentRowIndex++;

        if (CurrentRowIndex >= CurrentResult.Rows.Count)
        {
            return SqlReturn.NoData;
        }

        // Populate bound columns with current row data
        PopulateBoundColumns();

        return SqlReturn.Success;
    }

    /// <summary>
    /// Gets data from a column in the current row.
    /// </summary>
    /// <param name="columnNumber">The 1-based column number.</param>
    /// <param name="targetType">The target C data type.</param>
    /// <param name="value">The column value.</param>
    /// <param name="indicator">The indicator (length or SQL_NULL_DATA).</param>
    /// <returns>SQL_SUCCESS on success, SQL_ERROR on failure.</returns>
    public SqlReturn GetData(int columnNumber, SqlCType targetType, out object? value, out nint indicator)
    {
        ThrowIfDisposed();

        value = null;
        indicator = -1; // SQL_NULL_DATA

        if (CurrentResult == null || !CurrentResult.HasResultSet)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.InvalidCursorState,
                Message = "No result set is available.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        if (CurrentRowIndex < 0 || CurrentRowIndex >= CurrentResult.Rows.Count)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.InvalidCursorPosition,
                Message = "Cursor is not positioned on a valid row.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        if (columnNumber < 1 || columnNumber > CurrentResult.Columns.Count)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.ColumnNotFound,
                Message = $"Column number {columnNumber} is out of range.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        var row = CurrentResult.Rows[(int)CurrentRowIndex];
        var rawValue = row[columnNumber - 1];

        if (rawValue == null)
        {
            indicator = -1; // SQL_NULL_DATA
            return SqlReturn.Success;
        }

        // Convert to target type
        value = ConvertToTargetType(rawValue, targetType, out var length);
        indicator = length;

        return SqlReturn.Success;
    }

    /// <summary>
    /// Binds a parameter for prepared statement execution.
    /// </summary>
    /// <param name="parameterNumber">The 1-based parameter number.</param>
    /// <param name="parameter">The parameter binding information.</param>
    /// <returns>SQL_SUCCESS on success, SQL_ERROR on failure.</returns>
    public SqlReturn BindParameter(int parameterNumber, OdbcBoundParameter parameter)
    {
        ThrowIfDisposed();

        if (parameterNumber < 1)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.InvalidParameterNumber,
                Message = $"Invalid parameter number: {parameterNumber}",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        lock (_lock)
        {
            _boundParameters[parameterNumber] = parameter;
        }

        return SqlReturn.Success;
    }

    /// <summary>
    /// Binds a column for result fetching.
    /// </summary>
    /// <param name="columnNumber">The 1-based column number.</param>
    /// <param name="column">The column binding information.</param>
    /// <returns>SQL_SUCCESS on success, SQL_ERROR on failure.</returns>
    public SqlReturn BindColumn(int columnNumber, OdbcBoundColumn column)
    {
        ThrowIfDisposed();

        if (columnNumber < 1)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.ColumnNotFound,
                Message = $"Invalid column number: {columnNumber}",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        lock (_lock)
        {
            _boundColumns[columnNumber] = column;
        }

        return SqlReturn.Success;
    }

    /// <summary>
    /// Unbinds a parameter.
    /// </summary>
    /// <param name="parameterNumber">The 1-based parameter number.</param>
    /// <returns>SQL_SUCCESS on success.</returns>
    public SqlReturn UnbindParameter(int parameterNumber)
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            _boundParameters.Remove(parameterNumber);
        }

        return SqlReturn.Success;
    }

    /// <summary>
    /// Unbinds all columns.
    /// </summary>
    /// <returns>SQL_SUCCESS on success.</returns>
    public SqlReturn UnbindColumns()
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            _boundColumns.Clear();
        }

        return SqlReturn.Success;
    }

    /// <summary>
    /// Resets the statement for reuse.
    /// </summary>
    /// <param name="option">The reset option.</param>
    /// <returns>SQL_SUCCESS on success.</returns>
    public SqlReturn FreeStmt(FreeStmtOption option)
    {
        ThrowIfDisposed();

        switch (option)
        {
            case FreeStmtOption.Close:
                CurrentResult = null;
                CurrentRowIndex = -1;
                State = IsPrepared ? StatementState.Prepared : StatementState.Allocated;
                break;

            case FreeStmtOption.Unbind:
                _boundColumns.Clear();
                break;

            case FreeStmtOption.ResetParams:
                _boundParameters.Clear();
                break;
        }

        return SqlReturn.Success;
    }

    /// <summary>
    /// Describes a column in the result set.
    /// </summary>
    /// <param name="columnNumber">The 1-based column number.</param>
    /// <param name="columnInfo">The column information.</param>
    /// <returns>SQL_SUCCESS on success, SQL_ERROR on failure.</returns>
    public SqlReturn DescribeCol(int columnNumber, out OdbcColumnInfo? columnInfo)
    {
        ThrowIfDisposed();
        columnInfo = null;

        if (CurrentResult == null)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.InvalidCursorState,
                Message = "No result set metadata is available.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        if (columnNumber < 1 || columnNumber > CurrentResult.Columns.Count)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.ColumnNotFound,
                Message = $"Column number {columnNumber} is out of range.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        columnInfo = CurrentResult.Columns[columnNumber - 1];
        return SqlReturn.Success;
    }

    /// <summary>
    /// Gets the number of result columns.
    /// </summary>
    /// <param name="columnCount">The number of columns.</param>
    /// <returns>SQL_SUCCESS on success.</returns>
    public SqlReturn NumResultCols(out int columnCount)
    {
        ThrowIfDisposed();
        columnCount = CurrentResult?.Columns.Count ?? 0;
        return SqlReturn.Success;
    }

    /// <summary>
    /// Gets the row count affected by the last statement.
    /// </summary>
    /// <param name="rowCount">The row count.</param>
    /// <returns>SQL_SUCCESS on success.</returns>
    public SqlReturn RowCountResult(out long rowCount)
    {
        ThrowIfDisposed();
        rowCount = CurrentResult?.RowsAffected ?? -1;
        return SqlReturn.Success;
    }

    /// <summary>
    /// Sets a statement attribute.
    /// </summary>
    /// <param name="attribute">The attribute to set.</param>
    /// <param name="value">The attribute value.</param>
    /// <returns>SQL_SUCCESS on success, SQL_ERROR on failure.</returns>
    public SqlReturn SetAttribute(SqlStmtAttribute attribute, nint value)
    {
        ThrowIfDisposed();

        try
        {
            switch (attribute)
            {
                case SqlStmtAttribute.QueryTimeout:
                    QueryTimeout = (int)value;
                    break;

                case SqlStmtAttribute.MaxRows:
                    MaxRows = value;
                    break;

                case SqlStmtAttribute.CursorType:
                    CursorType = (CursorType)(int)value;
                    break;

                case SqlStmtAttribute.Concurrency:
                    Concurrency = (ConcurrencyType)(int)value;
                    break;

                case SqlStmtAttribute.UseBookmarks:
                    UseBookmarks = (int)value != 0;
                    break;

                case SqlStmtAttribute.RowArraySize:
                    RowArraySize = (int)value;
                    break;

                case SqlStmtAttribute.RowBindType:
                    RowBindType = (int)value;
                    break;

                case SqlStmtAttribute.ParamsetSize:
                    ParamsetSize = (int)value;
                    break;

                case SqlStmtAttribute.AsyncEnable:
                    AsyncEnabled = (int)value != 0;
                    break;

                default:
                    AddDiagnostic(new OdbcDiagnosticRecord
                    {
                        SqlState = SqlState.GeneralWarning,
                        Message = $"Attribute {attribute} not fully supported.",
                        NativeError = 0
                    });
                    return SqlReturn.SuccessWithInfo;
            }

            return SqlReturn.Success;
        }
        catch (Exception ex)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.GeneralError,
                Message = $"Error setting statement attribute: {ex.Message}",
                NativeError = 0
            });
            return SqlReturn.Error;
        }
    }

    /// <summary>
    /// Gets a statement attribute.
    /// </summary>
    /// <param name="attribute">The attribute to get.</param>
    /// <param name="value">The attribute value.</param>
    /// <returns>SQL_SUCCESS on success, SQL_ERROR on failure.</returns>
    public SqlReturn GetAttribute(SqlStmtAttribute attribute, out nint value)
    {
        ThrowIfDisposed();
        value = nint.Zero;

        try
        {
            switch (attribute)
            {
                case SqlStmtAttribute.QueryTimeout:
                    value = QueryTimeout;
                    break;

                case SqlStmtAttribute.MaxRows:
                    value = (nint)MaxRows;
                    break;

                case SqlStmtAttribute.CursorType:
                    value = (int)CursorType;
                    break;

                case SqlStmtAttribute.Concurrency:
                    value = (int)Concurrency;
                    break;

                case SqlStmtAttribute.UseBookmarks:
                    value = UseBookmarks ? 1 : 0;
                    break;

                case SqlStmtAttribute.RowArraySize:
                    value = RowArraySize;
                    break;

                case SqlStmtAttribute.RowBindType:
                    value = RowBindType;
                    break;

                case SqlStmtAttribute.ParamsetSize:
                    value = ParamsetSize;
                    break;

                case SqlStmtAttribute.AsyncEnable:
                    value = AsyncEnabled ? 1 : 0;
                    break;

                case SqlStmtAttribute.RowNumber:
                    value = (nint)(CurrentRowIndex + 1);
                    break;

                default:
                    AddDiagnostic(new OdbcDiagnosticRecord
                    {
                        SqlState = SqlState.InvalidAttrOrOptionId,
                        Message = $"Unknown statement attribute: {attribute}",
                        NativeError = 0
                    });
                    return SqlReturn.Error;
            }

            return SqlReturn.Success;
        }
        catch (Exception ex)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.GeneralError,
                Message = $"Error getting statement attribute: {ex.Message}",
                NativeError = 0
            });
            return SqlReturn.Error;
        }
    }

    private string SubstituteParameters(string sql)
    {
        if (_boundParameters.Count == 0)
            return sql;

        var result = sql;

        foreach (var kvp in _boundParameters.OrderByDescending(x => x.Key))
        {
            var placeholder = $"?";
            var value = FormatParameterValue(kvp.Value);

            // Replace the nth ? with the parameter value
            var index = FindNthOccurrence(result, '?', kvp.Key);
            if (index >= 0)
            {
                result = result.Substring(0, index) + value + result.Substring(index + 1);
            }
        }

        return result;
    }

    private static int FindNthOccurrence(string str, char ch, int n)
    {
        var count = 0;
        for (var i = 0; i < str.Length; i++)
        {
            if (str[i] == ch)
            {
                count++;
                if (count == n)
                    return i;
            }
        }
        return -1;
    }

    private static string FormatParameterValue(OdbcBoundParameter param)
    {
        if (param.Indicator == -1 || param.Value == null)
            return "NULL";

        return param.Value switch
        {
            string s => $"'{s.Replace("'", "''")}'",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
            bool b => b ? "1" : "0",
            _ => param.Value.ToString() ?? "NULL"
        };
    }

    private void PopulateBoundColumns()
    {
        if (CurrentResult == null || CurrentRowIndex < 0)
            return;

        var row = CurrentResult.Rows[(int)CurrentRowIndex];

        foreach (var kvp in _boundColumns)
        {
            var colNum = kvp.Key;
            var binding = kvp.Value;

            if (colNum > row.Length)
                continue;

            var rawValue = row[colNum - 1];

            if (rawValue == null)
            {
                binding.Value = null;
                binding.Indicator = -1; // SQL_NULL_DATA
            }
            else
            {
                binding.Value = ConvertToTargetType(rawValue, binding.TargetType, out var length);
                binding.Indicator = length;
            }
        }
    }

    private static object? ConvertToTargetType(object value, SqlCType targetType, out nint length)
    {
        length = 0;

        if (value == null)
        {
            length = -1; // SQL_NULL_DATA
            return null;
        }

        switch (targetType)
        {
            case SqlCType.Char:
            case SqlCType.WChar:
                var strValue = value.ToString() ?? "";
                length = strValue.Length;
                return strValue;

            case SqlCType.Long:
            case SqlCType.SLong:
                length = 4;
                return Convert.ToInt32(value);

            case SqlCType.Short:
            case SqlCType.SShort:
                length = 2;
                return Convert.ToInt16(value);

            case SqlCType.SBigInt:
                length = 8;
                return Convert.ToInt64(value);

            case SqlCType.Float:
                length = 4;
                return Convert.ToSingle(value);

            case SqlCType.Double:
                length = 8;
                return Convert.ToDouble(value);

            case SqlCType.Bit:
                length = 1;
                return Convert.ToBoolean(value);

            case SqlCType.Binary:
                if (value is byte[] bytes)
                {
                    length = bytes.Length;
                    return bytes;
                }
                var encoded = System.Text.Encoding.UTF8.GetBytes(value.ToString() ?? "");
                length = encoded.Length;
                return encoded;

            case SqlCType.TypeDate:
            case SqlCType.TypeTime:
            case SqlCType.TypeTimestamp:
                if (value is DateTime dt)
                {
                    length = 16; // Size of SQL_TIMESTAMP_STRUCT
                    return dt;
                }
                if (DateTime.TryParse(value.ToString(), out var parsed))
                {
                    length = 16;
                    return parsed;
                }
                length = -1;
                return null;

            case SqlCType.Guid:
                if (value is Guid g)
                {
                    length = 16;
                    return g;
                }
                if (Guid.TryParse(value.ToString(), out var parsedGuid))
                {
                    length = 16;
                    return parsedGuid;
                }
                length = -1;
                return null;

            default:
                var defaultStr = value.ToString() ?? "";
                length = defaultStr.Length;
                return defaultStr;
        }
    }

    /// <summary>
    /// Adds a diagnostic record to this handle.
    /// </summary>
    /// <param name="record">The diagnostic record to add.</param>
    public void AddDiagnostic(OdbcDiagnosticRecord record)
    {
        _diagnostics.Enqueue(record);
        while (_diagnostics.Count > 100)
        {
            _diagnostics.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Gets all diagnostic records.
    /// </summary>
    /// <returns>The diagnostic records.</returns>
    public IEnumerable<OdbcDiagnosticRecord> GetDiagnostics()
    {
        return _diagnostics.ToArray();
    }

    /// <summary>
    /// Clears all diagnostic records.
    /// </summary>
    public void ClearDiagnostics()
    {
        while (_diagnostics.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Gets a diagnostic record by index (1-based).
    /// </summary>
    /// <param name="recordNumber">The 1-based record number.</param>
    /// <returns>The diagnostic record, or null if not found.</returns>
    public OdbcDiagnosticRecord? GetDiagnostic(int recordNumber)
    {
        var records = _diagnostics.ToArray();
        if (recordNumber < 1 || recordNumber > records.Length)
        {
            return null;
        }
        return records[recordNumber - 1];
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(OdbcStatementHandle));
    }

    /// <summary>
    /// Disposes this statement handle and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CurrentResult = null;
        _boundParameters.Clear();
        _boundColumns.Clear();
        while (_diagnostics.TryDequeue(out _)) { }
    }
}

/// <summary>
/// Statement states.
/// </summary>
public enum StatementState
{
    /// <summary>
    /// Statement is allocated but not prepared.
    /// </summary>
    Allocated,

    /// <summary>
    /// Statement is prepared.
    /// </summary>
    Prepared,

    /// <summary>
    /// Statement has been executed.
    /// </summary>
    Executed,

    /// <summary>
    /// Statement has a result set.
    /// </summary>
    ResultSet
}

/// <summary>
/// Cursor types for ODBC.
/// </summary>
public enum CursorType
{
    /// <summary>
    /// Forward-only cursor.
    /// </summary>
    ForwardOnly = 0,

    /// <summary>
    /// Keyset-driven cursor.
    /// </summary>
    Keyset = 1,

    /// <summary>
    /// Dynamic cursor.
    /// </summary>
    Dynamic = 2,

    /// <summary>
    /// Static cursor.
    /// </summary>
    Static = 3
}

/// <summary>
/// Concurrency types for ODBC cursors.
/// </summary>
public enum ConcurrencyType
{
    /// <summary>
    /// Read-only cursor.
    /// </summary>
    ReadOnly = 1,

    /// <summary>
    /// Lock-based concurrency.
    /// </summary>
    Lock = 2,

    /// <summary>
    /// Row version-based concurrency.
    /// </summary>
    RowVer = 3,

    /// <summary>
    /// Value-based concurrency.
    /// </summary>
    Values = 4
}

/// <summary>
/// Options for SQLFreeStmt.
/// </summary>
public enum FreeStmtOption
{
    /// <summary>
    /// Close the cursor.
    /// </summary>
    Close = 0,

    /// <summary>
    /// Unbind all columns.
    /// </summary>
    Unbind = 2,

    /// <summary>
    /// Reset all parameters.
    /// </summary>
    ResetParams = 3
}
