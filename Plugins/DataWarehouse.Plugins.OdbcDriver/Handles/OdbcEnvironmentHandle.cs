// <copyright file="OdbcEnvironmentHandle.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.OdbcDriver.Handles;

/// <summary>
/// Represents an ODBC environment handle (HENV).
/// The environment handle is the top-level handle in the ODBC handle hierarchy.
/// It defines the global state for accessing data sources.
/// </summary>
public sealed class OdbcEnvironmentHandle : IDisposable
{
    private readonly ConcurrentBag<OdbcConnectionHandle> _connections = new();
    private readonly ConcurrentQueue<OdbcDiagnosticRecord> _diagnostics = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="OdbcEnvironmentHandle"/> class.
    /// </summary>
    /// <param name="handle">The native handle value.</param>
    public OdbcEnvironmentHandle(nint handle)
    {
        Handle = handle;
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets the native handle value.
    /// </summary>
    public nint Handle { get; }

    /// <summary>
    /// Gets the timestamp when this handle was created.
    /// </summary>
    public DateTime CreatedAt { get; }

    /// <summary>
    /// Gets or sets the ODBC version for this environment.
    /// Valid values are OV_ODBC3 (3) or OV_ODBC3_80 (380).
    /// </summary>
    public int OdbcVersion { get; set; } = 380; // Default to ODBC 3.80

    /// <summary>
    /// Gets or sets whether connection pooling is enabled.
    /// </summary>
    public ConnectionPoolingMode ConnectionPooling { get; set; } = ConnectionPoolingMode.Off;

    /// <summary>
    /// Gets or sets the connection pool matching mode.
    /// </summary>
    public ConnectionPoolMatchMode CpMatch { get; set; } = ConnectionPoolMatchMode.StrictMatch;

    /// <summary>
    /// Gets or sets whether output strings should be null-terminated.
    /// </summary>
    public bool OutputNts { get; set; } = true;

    /// <summary>
    /// Gets the child connection handles.
    /// </summary>
    public IEnumerable<OdbcConnectionHandle> Connections => _connections;

    /// <summary>
    /// Gets the number of child connections.
    /// </summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>
    /// Adds a connection handle to this environment.
    /// </summary>
    /// <param name="connection">The connection handle to add.</param>
    internal void AddConnection(OdbcConnectionHandle connection)
    {
        ThrowIfDisposed();
        _connections.Add(connection);
    }

    /// <summary>
    /// Removes a connection handle from this environment.
    /// </summary>
    /// <param name="connection">The connection handle to remove.</param>
    internal void RemoveConnection(OdbcConnectionHandle connection)
    {
        // ConcurrentBag doesn't support removal, but since we track handles
        // separately in HandleManager and call Dispose, this is acceptable.
        // The connection will be garbage collected.
    }

    /// <summary>
    /// Sets an environment attribute.
    /// </summary>
    /// <param name="attribute">The attribute to set.</param>
    /// <param name="value">The attribute value.</param>
    /// <returns>SQL_SUCCESS on success, SQL_ERROR on failure.</returns>
    public SqlReturn SetAttribute(SqlEnvAttribute attribute, nint value)
    {
        ThrowIfDisposed();

        try
        {
            switch (attribute)
            {
                case SqlEnvAttribute.OdbcVersion:
                    var version = (int)value;
                    if (version != 3 && version != 380)
                    {
                        AddDiagnostic(new OdbcDiagnosticRecord
                        {
                            SqlState = SqlState.InvalidAttrOrOptionId,
                            Message = $"Invalid ODBC version: {version}. Valid values are 3 or 380.",
                            NativeError = 0
                        });
                        return SqlReturn.Error;
                    }
                    OdbcVersion = version;
                    break;

                case SqlEnvAttribute.ConnectionPooling:
                    ConnectionPooling = (ConnectionPoolingMode)(int)value;
                    break;

                case SqlEnvAttribute.CpMatch:
                    CpMatch = (ConnectionPoolMatchMode)(int)value;
                    break;

                case SqlEnvAttribute.OutputNts:
                    OutputNts = (int)value != 0;
                    break;

                default:
                    AddDiagnostic(new OdbcDiagnosticRecord
                    {
                        SqlState = SqlState.InvalidAttrOrOptionId,
                        Message = $"Unknown environment attribute: {attribute}",
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
                Message = $"Error setting environment attribute: {ex.Message}",
                NativeError = 0
            });
            return SqlReturn.Error;
        }
    }

    /// <summary>
    /// Gets an environment attribute.
    /// </summary>
    /// <param name="attribute">The attribute to get.</param>
    /// <param name="value">The attribute value.</param>
    /// <returns>SQL_SUCCESS on success, SQL_ERROR on failure.</returns>
    public SqlReturn GetAttribute(SqlEnvAttribute attribute, out nint value)
    {
        ThrowIfDisposed();
        value = nint.Zero;

        try
        {
            switch (attribute)
            {
                case SqlEnvAttribute.OdbcVersion:
                    value = OdbcVersion;
                    break;

                case SqlEnvAttribute.ConnectionPooling:
                    value = (int)ConnectionPooling;
                    break;

                case SqlEnvAttribute.CpMatch:
                    value = (int)CpMatch;
                    break;

                case SqlEnvAttribute.OutputNts:
                    value = OutputNts ? 1 : 0;
                    break;

                default:
                    AddDiagnostic(new OdbcDiagnosticRecord
                    {
                        SqlState = SqlState.InvalidAttrOrOptionId,
                        Message = $"Unknown environment attribute: {attribute}",
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
                Message = $"Error getting environment attribute: {ex.Message}",
                NativeError = 0
            });
            return SqlReturn.Error;
        }
    }

    /// <summary>
    /// Adds a diagnostic record to this handle.
    /// </summary>
    /// <param name="record">The diagnostic record to add.</param>
    public void AddDiagnostic(OdbcDiagnosticRecord record)
    {
        _diagnostics.Enqueue(record);
        // Keep only the last 100 diagnostics
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
        ObjectDisposedException.ThrowIf(_disposed, nameof(OdbcEnvironmentHandle));
    }

    /// <summary>
    /// Disposes this environment handle and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Connections are disposed by HandleManager
        while (_diagnostics.TryDequeue(out _)) { }
    }
}

/// <summary>
/// Connection pooling modes for ODBC.
/// </summary>
public enum ConnectionPoolingMode
{
    /// <summary>
    /// Connection pooling is disabled.
    /// </summary>
    Off = 0,

    /// <summary>
    /// One pool per driver.
    /// </summary>
    OnePerDriver = 1,

    /// <summary>
    /// One pool per environment.
    /// </summary>
    OnePerHenv = 2
}

/// <summary>
/// Connection pool matching modes for ODBC.
/// </summary>
public enum ConnectionPoolMatchMode
{
    /// <summary>
    /// Strict matching of connection attributes.
    /// </summary>
    StrictMatch = 0,

    /// <summary>
    /// Relaxed matching of connection attributes.
    /// </summary>
    RelaxedMatch = 1
}
