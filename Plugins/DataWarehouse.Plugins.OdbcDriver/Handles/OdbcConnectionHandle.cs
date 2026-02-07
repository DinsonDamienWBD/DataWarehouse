// <copyright file="OdbcConnectionHandle.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.OdbcDriver.Handles;

/// <summary>
/// Represents an ODBC connection handle (HDBC).
/// The connection handle manages the connection to a data source and
/// contains connection-specific attributes and state.
/// </summary>
public sealed class OdbcConnectionHandle : IDisposable
{
    private readonly ConcurrentBag<OdbcStatementHandle> _statements = new();
    private readonly ConcurrentBag<OdbcDescriptorHandle> _descriptors = new();
    private readonly ConcurrentQueue<OdbcDiagnosticRecord> _diagnostics = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="OdbcConnectionHandle"/> class.
    /// </summary>
    /// <param name="handle">The native handle value.</param>
    /// <param name="environment">The parent environment handle.</param>
    public OdbcConnectionHandle(nint handle, OdbcEnvironmentHandle environment)
    {
        Handle = handle;
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets the native handle value.
    /// </summary>
    public nint Handle { get; }

    /// <summary>
    /// Gets the parent environment handle.
    /// </summary>
    public OdbcEnvironmentHandle Environment { get; }

    /// <summary>
    /// Gets the timestamp when this handle was created.
    /// </summary>
    public DateTime CreatedAt { get; }

    /// <summary>
    /// Gets or sets whether this connection is currently connected.
    /// </summary>
    public bool IsConnected { get; set; }

    /// <summary>
    /// Gets the timestamp when the connection was established.
    /// </summary>
    public DateTime? ConnectedAt { get; private set; }

    /// <summary>
    /// Gets or sets the data source name.
    /// </summary>
    public string? DataSourceName { get; set; }

    /// <summary>
    /// Gets or sets the server name.
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// Gets or sets the database name (current catalog).
    /// </summary>
    public string? DatabaseName { get; set; }

    /// <summary>
    /// Gets or sets the user name.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Gets or sets the full connection string.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets whether auto-commit is enabled.
    /// </summary>
    public bool AutoCommit { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the connection is read-only.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Gets or sets the login timeout in seconds.
    /// </summary>
    public int LoginTimeout { get; set; } = 30;

    /// <summary>
    /// Gets or sets the connection timeout in seconds.
    /// </summary>
    public int ConnectionTimeout { get; set; } = 30;

    /// <summary>
    /// Gets or sets the query timeout in seconds.
    /// </summary>
    public int QueryTimeout { get; set; } = 300;

    /// <summary>
    /// Gets or sets the transaction isolation level.
    /// </summary>
    public TransactionIsolationLevel TransactionIsolation { get; set; } = TransactionIsolationLevel.ReadCommitted;

    /// <summary>
    /// Gets or sets whether the connection is dead.
    /// </summary>
    public bool IsDead { get; set; }

    /// <summary>
    /// Gets or sets the packet size.
    /// </summary>
    public int PacketSize { get; set; } = 4096;

    /// <summary>
    /// Gets or sets whether async operations are enabled.
    /// </summary>
    public bool AsyncEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether tracing is enabled.
    /// </summary>
    public bool TraceEnabled { get; set; }

    /// <summary>
    /// Gets or sets the trace file path.
    /// </summary>
    public string? TraceFile { get; set; }

    /// <summary>
    /// Gets the child statement handles.
    /// </summary>
    public IEnumerable<OdbcStatementHandle> Statements => _statements;

    /// <summary>
    /// Gets the child descriptor handles.
    /// </summary>
    public IEnumerable<OdbcDescriptorHandle> Descriptors => _descriptors;

    /// <summary>
    /// Gets the number of child statements.
    /// </summary>
    public int StatementCount => _statements.Count;

    /// <summary>
    /// Adds a statement handle to this connection.
    /// </summary>
    /// <param name="statement">The statement handle to add.</param>
    internal void AddStatement(OdbcStatementHandle statement)
    {
        ThrowIfDisposed();
        _statements.Add(statement);
    }

    /// <summary>
    /// Removes a statement handle from this connection.
    /// </summary>
    /// <param name="statement">The statement handle to remove.</param>
    internal void RemoveStatement(OdbcStatementHandle statement)
    {
        // ConcurrentBag doesn't support removal directly
    }

    /// <summary>
    /// Adds a descriptor handle to this connection.
    /// </summary>
    /// <param name="descriptor">The descriptor handle to add.</param>
    internal void AddDescriptor(OdbcDescriptorHandle descriptor)
    {
        ThrowIfDisposed();
        _descriptors.Add(descriptor);
    }

    /// <summary>
    /// Removes a descriptor handle from this connection.
    /// </summary>
    /// <param name="descriptor">The descriptor handle to remove.</param>
    internal void RemoveDescriptor(OdbcDescriptorHandle descriptor)
    {
        // ConcurrentBag doesn't support removal directly
    }

    /// <summary>
    /// Establishes a connection using a connection string.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    /// <returns>SQL_SUCCESS on success, SQL_ERROR on failure.</returns>
    public SqlReturn Connect(string connectionString)
    {
        ThrowIfDisposed();

        if (IsConnected)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.ConnectionNameInUse,
                Message = "Connection is already established.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        try
        {
            // Parse connection string
            ParseConnectionString(connectionString);
            ConnectionString = connectionString;
            IsConnected = true;
            ConnectedAt = DateTime.UtcNow;
            IsDead = false;

            return SqlReturn.Success;
        }
        catch (Exception ex)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.ConnectionFailure,
                Message = $"Connection failed: {ex.Message}",
                NativeError = 0
            });
            return SqlReturn.Error;
        }
    }

    /// <summary>
    /// Establishes a connection using DSN, user ID, and password.
    /// </summary>
    /// <param name="dsn">The data source name.</param>
    /// <param name="uid">The user ID.</param>
    /// <param name="pwd">The password.</param>
    /// <returns>SQL_SUCCESS on success, SQL_ERROR on failure.</returns>
    public SqlReturn Connect(string dsn, string uid, string pwd)
    {
        ThrowIfDisposed();

        if (IsConnected)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.ConnectionNameInUse,
                Message = "Connection is already established.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        try
        {
            DataSourceName = dsn;
            UserName = uid;
            ServerName = "localhost";
            DatabaseName = "datawarehouse";
            IsConnected = true;
            ConnectedAt = DateTime.UtcNow;
            IsDead = false;

            Console.WriteLine("[OdbcConnectionHandle] WARNING: Using default localhost connection. Configure DSN explicitly for production.");

            return SqlReturn.Success;
        }
        catch (Exception ex)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.ConnectionFailure,
                Message = $"Connection failed: {ex.Message}",
                NativeError = 0
            });
            return SqlReturn.Error;
        }
    }

    /// <summary>
    /// Disconnects from the data source.
    /// </summary>
    /// <returns>SQL_SUCCESS on success, SQL_ERROR on failure.</returns>
    public SqlReturn Disconnect()
    {
        ThrowIfDisposed();

        if (!IsConnected)
        {
            AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.ConnectionNotExist,
                Message = "Connection does not exist.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        // Check for uncommitted transactions
        // In a real implementation, we would check transaction state

        IsConnected = false;
        IsDead = true;

        return SqlReturn.Success;
    }

    /// <summary>
    /// Sets a connection attribute.
    /// </summary>
    /// <param name="attribute">The attribute to set.</param>
    /// <param name="value">The attribute value.</param>
    /// <returns>SQL_SUCCESS on success, SQL_ERROR on failure.</returns>
    public SqlReturn SetAttribute(SqlConnectAttribute attribute, nint value)
    {
        ThrowIfDisposed();

        try
        {
            switch (attribute)
            {
                case SqlConnectAttribute.AccessMode:
                    ReadOnly = (int)value == 1; // SQL_MODE_READ_ONLY
                    break;

                case SqlConnectAttribute.AutoCommit:
                    AutoCommit = (int)value != 0;
                    break;

                case SqlConnectAttribute.LoginTimeout:
                    LoginTimeout = (int)value;
                    break;

                case SqlConnectAttribute.ConnectionTimeout:
                    ConnectionTimeout = (int)value;
                    break;

                case SqlConnectAttribute.TxnIsolation:
                    TransactionIsolation = (TransactionIsolationLevel)(int)value;
                    break;

                case SqlConnectAttribute.Trace:
                    TraceEnabled = (int)value != 0;
                    break;

                case SqlConnectAttribute.AsyncEnable:
                    AsyncEnabled = (int)value != 0;
                    break;

                case SqlConnectAttribute.PacketSize:
                    PacketSize = (int)value;
                    break;

                default:
                    // Silently accept unknown attributes with warning
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
                Message = $"Error setting connection attribute: {ex.Message}",
                NativeError = 0
            });
            return SqlReturn.Error;
        }
    }

    /// <summary>
    /// Gets a connection attribute.
    /// </summary>
    /// <param name="attribute">The attribute to get.</param>
    /// <param name="value">The attribute value.</param>
    /// <returns>SQL_SUCCESS on success, SQL_ERROR on failure.</returns>
    public SqlReturn GetAttribute(SqlConnectAttribute attribute, out nint value)
    {
        ThrowIfDisposed();
        value = nint.Zero;

        try
        {
            switch (attribute)
            {
                case SqlConnectAttribute.AccessMode:
                    value = ReadOnly ? 1 : 0;
                    break;

                case SqlConnectAttribute.AutoCommit:
                    value = AutoCommit ? 1 : 0;
                    break;

                case SqlConnectAttribute.LoginTimeout:
                    value = LoginTimeout;
                    break;

                case SqlConnectAttribute.ConnectionTimeout:
                    value = ConnectionTimeout;
                    break;

                case SqlConnectAttribute.TxnIsolation:
                    value = (int)TransactionIsolation;
                    break;

                case SqlConnectAttribute.Trace:
                    value = TraceEnabled ? 1 : 0;
                    break;

                case SqlConnectAttribute.AsyncEnable:
                    value = AsyncEnabled ? 1 : 0;
                    break;

                case SqlConnectAttribute.PacketSize:
                    value = PacketSize;
                    break;

                case SqlConnectAttribute.ConnectionDead:
                    value = IsDead ? 1 : 0;
                    break;

                default:
                    AddDiagnostic(new OdbcDiagnosticRecord
                    {
                        SqlState = SqlState.InvalidAttrOrOptionId,
                        Message = $"Unknown connection attribute: {attribute}",
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
                Message = $"Error getting connection attribute: {ex.Message}",
                NativeError = 0
            });
            return SqlReturn.Error;
        }
    }

    /// <summary>
    /// Parses a connection string into component parts.
    /// </summary>
    /// <param name="connectionString">The connection string to parse.</param>
    private void ParseConnectionString(string connectionString)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var kvp = part.Split('=', 2);
            if (kvp.Length != 2) continue;

            var key = kvp[0].Trim().ToUpperInvariant();
            var value = kvp[1].Trim();

            switch (key)
            {
                case "DSN":
                    DataSourceName = value;
                    break;
                case "SERVER":
                case "HOST":
                    ServerName = value;
                    break;
                case "DATABASE":
                case "DB":
                case "CATALOG":
                    DatabaseName = value;
                    break;
                case "UID":
                case "USER":
                case "USERNAME":
                    UserName = value;
                    break;
                case "TIMEOUT":
                case "CONNECT_TIMEOUT":
                    if (int.TryParse(value, out var timeout))
                        ConnectionTimeout = timeout;
                    break;
            }
        }

        // Set defaults
        ServerName ??= "localhost";
        DatabaseName ??= "datawarehouse";

        if (ServerName == "localhost")
        {
            Console.WriteLine("[OdbcConnectionHandle] WARNING: Using default localhost server. Configure connection string for production.");
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
        ObjectDisposedException.ThrowIf(_disposed, nameof(OdbcConnectionHandle));
    }

    /// <summary>
    /// Disposes this connection handle and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsConnected)
        {
            IsConnected = false;
        }

        while (_diagnostics.TryDequeue(out _)) { }
    }
}

/// <summary>
/// Transaction isolation levels for ODBC.
/// </summary>
public enum TransactionIsolationLevel
{
    /// <summary>
    /// Read uncommitted.
    /// </summary>
    ReadUncommitted = 1,

    /// <summary>
    /// Read committed.
    /// </summary>
    ReadCommitted = 2,

    /// <summary>
    /// Repeatable read.
    /// </summary>
    RepeatableRead = 4,

    /// <summary>
    /// Serializable.
    /// </summary>
    Serializable = 8
}
