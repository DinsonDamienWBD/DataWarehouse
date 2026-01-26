// <copyright file="HandleManager.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.OdbcDriver.Handles;

/// <summary>
/// Thread-safe manager for ODBC handles.
/// Manages the lifecycle and relationships between environment, connection,
/// statement, and descriptor handles according to ODBC 3.8 specification.
/// </summary>
public sealed class HandleManager : IDisposable
{
    private readonly ConcurrentDictionary<nint, OdbcEnvironmentHandle> _environments = new();
    private readonly ConcurrentDictionary<nint, OdbcConnectionHandle> _connections = new();
    private readonly ConcurrentDictionary<nint, OdbcStatementHandle> _statements = new();
    private readonly ConcurrentDictionary<nint, OdbcDescriptorHandle> _descriptors = new();
    private readonly object _handleLock = new();
    private nint _nextHandle = 1000;
    private bool _disposed;

    /// <summary>
    /// Gets the singleton instance of the handle manager.
    /// </summary>
    public static HandleManager Instance { get; } = new();

    /// <summary>
    /// Allocates a new environment handle.
    /// </summary>
    /// <param name="handle">The allocated handle.</param>
    /// <returns>SQL_SUCCESS on success, SQL_ERROR on failure.</returns>
    public SqlReturn AllocateEnvironmentHandle(out nint handle)
    {
        ThrowIfDisposed();

        handle = nint.Zero;

        try
        {
            var newHandle = AllocateHandleId();
            var envHandle = new OdbcEnvironmentHandle(newHandle);

            if (!_environments.TryAdd(newHandle, envHandle))
            {
                return SqlReturn.Error;
            }

            handle = newHandle;
            return SqlReturn.Success;
        }
        catch
        {
            return SqlReturn.Error;
        }
    }

    /// <summary>
    /// Allocates a new connection handle associated with an environment.
    /// </summary>
    /// <param name="environmentHandle">The parent environment handle.</param>
    /// <param name="handle">The allocated connection handle.</param>
    /// <returns>SQL_SUCCESS on success, SQL_INVALID_HANDLE or SQL_ERROR on failure.</returns>
    public SqlReturn AllocateConnectionHandle(nint environmentHandle, out nint handle)
    {
        ThrowIfDisposed();

        handle = nint.Zero;

        if (!_environments.TryGetValue(environmentHandle, out var env))
        {
            return SqlReturn.InvalidHandle;
        }

        try
        {
            var newHandle = AllocateHandleId();
            var connHandle = new OdbcConnectionHandle(newHandle, env);

            if (!_connections.TryAdd(newHandle, connHandle))
            {
                return SqlReturn.Error;
            }

            env.AddConnection(connHandle);
            handle = newHandle;
            return SqlReturn.Success;
        }
        catch
        {
            return SqlReturn.Error;
        }
    }

    /// <summary>
    /// Allocates a new statement handle associated with a connection.
    /// </summary>
    /// <param name="connectionHandle">The parent connection handle.</param>
    /// <param name="handle">The allocated statement handle.</param>
    /// <returns>SQL_SUCCESS on success, SQL_INVALID_HANDLE or SQL_ERROR on failure.</returns>
    public SqlReturn AllocateStatementHandle(nint connectionHandle, out nint handle)
    {
        ThrowIfDisposed();

        handle = nint.Zero;

        if (!_connections.TryGetValue(connectionHandle, out var conn))
        {
            return SqlReturn.InvalidHandle;
        }

        if (!conn.IsConnected)
        {
            conn.AddDiagnostic(new OdbcDiagnosticRecord
            {
                SqlState = SqlState.ConnectionNotExist,
                Message = "Connection does not exist. Cannot allocate statement.",
                NativeError = 0
            });
            return SqlReturn.Error;
        }

        try
        {
            var newHandle = AllocateHandleId();
            var stmtHandle = new OdbcStatementHandle(newHandle, conn);

            if (!_statements.TryAdd(newHandle, stmtHandle))
            {
                return SqlReturn.Error;
            }

            conn.AddStatement(stmtHandle);
            handle = newHandle;
            return SqlReturn.Success;
        }
        catch
        {
            return SqlReturn.Error;
        }
    }

    /// <summary>
    /// Allocates a new descriptor handle associated with a connection.
    /// </summary>
    /// <param name="connectionHandle">The parent connection handle.</param>
    /// <param name="handle">The allocated descriptor handle.</param>
    /// <returns>SQL_SUCCESS on success, SQL_INVALID_HANDLE or SQL_ERROR on failure.</returns>
    public SqlReturn AllocateDescriptorHandle(nint connectionHandle, out nint handle)
    {
        ThrowIfDisposed();

        handle = nint.Zero;

        if (!_connections.TryGetValue(connectionHandle, out var conn))
        {
            return SqlReturn.InvalidHandle;
        }

        try
        {
            var newHandle = AllocateHandleId();
            var descHandle = new OdbcDescriptorHandle(newHandle, conn);

            if (!_descriptors.TryAdd(newHandle, descHandle))
            {
                return SqlReturn.Error;
            }

            conn.AddDescriptor(descHandle);
            handle = newHandle;
            return SqlReturn.Success;
        }
        catch
        {
            return SqlReturn.Error;
        }
    }

    /// <summary>
    /// Frees a handle and all child handles.
    /// </summary>
    /// <param name="handleType">The type of handle to free.</param>
    /// <param name="handle">The handle to free.</param>
    /// <returns>SQL_SUCCESS on success, SQL_INVALID_HANDLE on failure.</returns>
    public SqlReturn FreeHandle(SqlHandleType handleType, nint handle)
    {
        ThrowIfDisposed();

        return handleType switch
        {
            SqlHandleType.Environment => FreeEnvironmentHandle(handle),
            SqlHandleType.Connection => FreeConnectionHandle(handle),
            SqlHandleType.Statement => FreeStatementHandle(handle),
            SqlHandleType.Descriptor => FreeDescriptorHandle(handle),
            _ => SqlReturn.InvalidHandle
        };
    }

    /// <summary>
    /// Gets an environment handle by its ID.
    /// </summary>
    /// <param name="handle">The handle ID.</param>
    /// <returns>The environment handle, or null if not found.</returns>
    public OdbcEnvironmentHandle? GetEnvironmentHandle(nint handle)
    {
        ThrowIfDisposed();
        _environments.TryGetValue(handle, out var env);
        return env;
    }

    /// <summary>
    /// Gets a connection handle by its ID.
    /// </summary>
    /// <param name="handle">The handle ID.</param>
    /// <returns>The connection handle, or null if not found.</returns>
    public OdbcConnectionHandle? GetConnectionHandle(nint handle)
    {
        ThrowIfDisposed();
        _connections.TryGetValue(handle, out var conn);
        return conn;
    }

    /// <summary>
    /// Gets a statement handle by its ID.
    /// </summary>
    /// <param name="handle">The handle ID.</param>
    /// <returns>The statement handle, or null if not found.</returns>
    public OdbcStatementHandle? GetStatementHandle(nint handle)
    {
        ThrowIfDisposed();
        _statements.TryGetValue(handle, out var stmt);
        return stmt;
    }

    /// <summary>
    /// Gets a descriptor handle by its ID.
    /// </summary>
    /// <param name="handle">The handle ID.</param>
    /// <returns>The descriptor handle, or null if not found.</returns>
    public OdbcDescriptorHandle? GetDescriptorHandle(nint handle)
    {
        ThrowIfDisposed();
        _descriptors.TryGetValue(handle, out var desc);
        return desc;
    }

    /// <summary>
    /// Validates that a handle exists and is of the expected type.
    /// </summary>
    /// <param name="handleType">The expected handle type.</param>
    /// <param name="handle">The handle to validate.</param>
    /// <returns>True if valid, false otherwise.</returns>
    public bool ValidateHandle(SqlHandleType handleType, nint handle)
    {
        ThrowIfDisposed();

        return handleType switch
        {
            SqlHandleType.Environment => _environments.ContainsKey(handle),
            SqlHandleType.Connection => _connections.ContainsKey(handle),
            SqlHandleType.Statement => _statements.ContainsKey(handle),
            SqlHandleType.Descriptor => _descriptors.ContainsKey(handle),
            _ => false
        };
    }

    /// <summary>
    /// Gets the count of active handles of a specific type.
    /// </summary>
    /// <param name="handleType">The handle type to count.</param>
    /// <returns>The count of active handles.</returns>
    public int GetHandleCount(SqlHandleType handleType)
    {
        ThrowIfDisposed();

        return handleType switch
        {
            SqlHandleType.Environment => _environments.Count,
            SqlHandleType.Connection => _connections.Count,
            SqlHandleType.Statement => _statements.Count,
            SqlHandleType.Descriptor => _descriptors.Count,
            _ => 0
        };
    }

    private nint AllocateHandleId()
    {
        lock (_handleLock)
        {
            return _nextHandle++;
        }
    }

    private SqlReturn FreeEnvironmentHandle(nint handle)
    {
        if (!_environments.TryRemove(handle, out var env))
        {
            return SqlReturn.InvalidHandle;
        }

        // Free all child connections (which will free their children)
        foreach (var conn in env.Connections.ToList())
        {
            FreeConnectionHandle(conn.Handle);
        }

        env.Dispose();
        return SqlReturn.Success;
    }

    private SqlReturn FreeConnectionHandle(nint handle)
    {
        if (!_connections.TryRemove(handle, out var conn))
        {
            return SqlReturn.InvalidHandle;
        }

        // Free all child statements
        foreach (var stmt in conn.Statements.ToList())
        {
            FreeStatementHandle(stmt.Handle);
        }

        // Free all child descriptors
        foreach (var desc in conn.Descriptors.ToList())
        {
            FreeDescriptorHandle(desc.Handle);
        }

        // Remove from parent environment
        conn.Environment.RemoveConnection(conn);

        conn.Dispose();
        return SqlReturn.Success;
    }

    private SqlReturn FreeStatementHandle(nint handle)
    {
        if (!_statements.TryRemove(handle, out var stmt))
        {
            return SqlReturn.InvalidHandle;
        }

        // Remove from parent connection
        stmt.Connection.RemoveStatement(stmt);

        stmt.Dispose();
        return SqlReturn.Success;
    }

    private SqlReturn FreeDescriptorHandle(nint handle)
    {
        if (!_descriptors.TryRemove(handle, out var desc))
        {
            return SqlReturn.InvalidHandle;
        }

        // Remove from parent connection
        desc.Connection.RemoveDescriptor(desc);

        desc.Dispose();
        return SqlReturn.Success;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(HandleManager));
    }

    /// <summary>
    /// Disposes all handles and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Dispose all handles in reverse order
        foreach (var stmt in _statements.Values)
        {
            stmt.Dispose();
        }
        _statements.Clear();

        foreach (var desc in _descriptors.Values)
        {
            desc.Dispose();
        }
        _descriptors.Clear();

        foreach (var conn in _connections.Values)
        {
            conn.Dispose();
        }
        _connections.Clear();

        foreach (var env in _environments.Values)
        {
            env.Dispose();
        }
        _environments.Clear();
    }
}
