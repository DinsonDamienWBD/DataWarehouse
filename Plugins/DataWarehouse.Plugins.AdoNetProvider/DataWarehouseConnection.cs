using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace DataWarehouse.Plugins.AdoNetProvider;

/// <summary>
/// Represents a connection to a DataWarehouse database.
/// Supports connection pooling, transactions, and async operations.
/// </summary>
/// <remarks>
/// Connections should be disposed after use. When using connection pooling,
/// the underlying connection is returned to the pool rather than being closed.
/// This class is not thread-safe; each thread should use its own connection.
/// </remarks>
public sealed class DataWarehouseConnection : DbConnection
{
    private string _connectionString = string.Empty;
    private ConnectionState _state = ConnectionState.Closed;
    private PooledConnection? _pooledConnection;
    private DataWarehouseTransaction? _transaction;
    private string _database = string.Empty;
    private string _serverVersion = string.Empty;
    private bool _disposed;
    private readonly object _lock = new();

    /// <inheritdoc/>
    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (_state != ConnectionState.Closed)
                throw new InvalidOperationException("Cannot change connection string while the connection is open.");
            _connectionString = value ?? string.Empty;
        }
    }

    /// <inheritdoc/>
    public override string Database => _database;

    /// <inheritdoc/>
    public override string DataSource
    {
        get
        {
            if (string.IsNullOrEmpty(_connectionString))
                return string.Empty;

            var builder = new DataWarehouseConnectionStringBuilder(_connectionString);
            return $"{builder.Server}:{builder.Port}";
        }
    }

    /// <inheritdoc/>
    public override string ServerVersion => _serverVersion;

    /// <inheritdoc/>
    public override ConnectionState State => _state;

    /// <summary>
    /// Gets the current transaction associated with this connection.
    /// </summary>
    public DataWarehouseTransaction? CurrentTransaction => _transaction;

    /// <summary>
    /// Gets the connection string builder for this connection.
    /// </summary>
    public DataWarehouseConnectionStringBuilder ConnectionStringBuilder =>
        new DataWarehouseConnectionStringBuilder(_connectionString);

    /// <summary>
    /// Initializes a new instance of the DataWarehouseConnection class.
    /// </summary>
    public DataWarehouseConnection()
    {
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseConnection class with a connection string.
    /// </summary>
    /// <param name="connectionString">The connection string to use.</param>
    public DataWarehouseConnection(string connectionString)
    {
        ConnectionString = connectionString;
    }

    /// <inheritdoc/>
    public override void Open()
    {
        OpenAsync().GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (_state == ConnectionState.Open)
            return;

        if (string.IsNullOrEmpty(_connectionString))
            throw new InvalidOperationException("ConnectionString property must be set.");

        lock (_lock)
        {
            if (_state != ConnectionState.Closed)
                return;
            _state = ConnectionState.Connecting;
        }

        try
        {
            var builder = new DataWarehouseConnectionStringBuilder(_connectionString);

            if (builder.Pooling)
            {
                var pool = ConnectionPoolManager.GetPool(_connectionString);
                _pooledConnection = await pool.AcquireAsync(cancellationToken);
            }
            else
            {
                // Direct connection without pooling
                _pooledConnection = await CreateDirectConnectionAsync(builder, cancellationToken);
            }

            _database = builder.Database;
            _serverVersion = "14.0"; // Would be retrieved from server during handshake

            lock (_lock)
            {
                _state = ConnectionState.Open;
            }
        }
        catch
        {
            lock (_lock)
            {
                _state = ConnectionState.Closed;
            }
            throw;
        }
    }

    /// <inheritdoc/>
    public override void Close()
    {
        CloseAsync().GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public override async Task CloseAsync()
    {
        if (_state == ConnectionState.Closed)
            return;

        // Rollback any active transaction
        if (_transaction != null && !_transaction.IsCompleted)
        {
            try
            {
                await _transaction.RollbackAsync();
            }
            catch
            {
                // Ignore errors during close
            }
        }
        _transaction = null;

        // Return connection to pool or close it
        if (_pooledConnection != null)
        {
            var builder = new DataWarehouseConnectionStringBuilder(_connectionString);
            if (builder.Pooling)
            {
                var pool = ConnectionPoolManager.GetPool(_connectionString);
                await pool.ReleaseAsync(_pooledConnection);
            }
            else
            {
                await _pooledConnection.CloseInternalAsync();
                _pooledConnection.Dispose();
            }
            _pooledConnection = null;
        }

        lock (_lock)
        {
            _state = ConnectionState.Closed;
        }
    }

    /// <inheritdoc/>
    public override void ChangeDatabase(string databaseName)
    {
        ChangeDatabaseAsync(databaseName).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously changes the current database.
    /// </summary>
    /// <param name="databaseName">The name of the database to use.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task ChangeDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(databaseName);
        ThrowIfDisposed();

        if (_state != ConnectionState.Open)
            throw new InvalidOperationException("Connection must be open to change database.");

        // Close current connection and open with new database
        var builder = new DataWarehouseConnectionStringBuilder(_connectionString)
        {
            Database = databaseName
        };

        var newConnectionString = builder.ConnectionString;

        await CloseAsync();
        _connectionString = newConnectionString;
        await OpenAsync(cancellationToken);
    }

    /// <inheritdoc/>
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        return BeginTransaction(isolationLevel);
    }

    /// <summary>
    /// Begins a database transaction.
    /// </summary>
    /// <returns>A new transaction.</returns>
    public new DataWarehouseTransaction BeginTransaction()
    {
        return BeginTransaction(IsolationLevel.ReadCommitted);
    }

    /// <summary>
    /// Begins a database transaction with the specified isolation level.
    /// </summary>
    /// <param name="isolationLevel">The isolation level for the transaction.</param>
    /// <returns>A new transaction.</returns>
    public new DataWarehouseTransaction BeginTransaction(IsolationLevel isolationLevel)
    {
        return BeginTransactionAsync(isolationLevel).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously begins a database transaction.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that returns a new transaction.</returns>
    public async ValueTask<DataWarehouseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        return await BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
    }

    /// <summary>
    /// Asynchronously begins a database transaction with the specified isolation level.
    /// </summary>
    /// <param name="isolationLevel">The isolation level for the transaction.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that returns a new transaction.</returns>
    public async ValueTask<DataWarehouseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_state != ConnectionState.Open)
            throw new InvalidOperationException("Connection must be open to begin a transaction.");

        if (_transaction != null && !_transaction.IsCompleted)
            throw new InvalidOperationException("A transaction is already in progress.");

        _transaction = new DataWarehouseTransaction(this, isolationLevel);
        await _transaction.BeginAsync(cancellationToken);
        return _transaction;
    }

    /// <inheritdoc/>
    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        return await BeginTransactionAsync(isolationLevel, cancellationToken);
    }

    /// <inheritdoc/>
    protected override DbCommand CreateDbCommand()
    {
        return CreateCommand();
    }

    /// <summary>
    /// Creates a new command associated with this connection.
    /// </summary>
    /// <returns>A new DataWarehouseCommand.</returns>
    public new DataWarehouseCommand CreateCommand()
    {
        return new DataWarehouseCommand
        {
            Connection = this,
            Transaction = _transaction
        };
    }

    /// <summary>
    /// Creates a new command with the specified command text.
    /// </summary>
    /// <param name="commandText">The command text.</param>
    /// <returns>A new DataWarehouseCommand.</returns>
    public DataWarehouseCommand CreateCommand(string commandText)
    {
        return new DataWarehouseCommand(commandText, this);
    }

    /// <summary>
    /// Gets the pooled connection for internal use.
    /// </summary>
    internal PooledConnection? GetPooledConnection()
    {
        return _pooledConnection;
    }

    /// <summary>
    /// Clears the current transaction reference.
    /// </summary>
    internal void ClearTransaction()
    {
        _transaction = null;
    }

    /// <summary>
    /// Clears all pooled connections for this connection string.
    /// </summary>
    public static async Task ClearPoolAsync(string connectionString)
    {
        await ConnectionPoolManager.ClearPoolAsync(connectionString);
    }

    /// <summary>
    /// Clears all connection pools.
    /// </summary>
    public static async Task ClearAllPoolsAsync()
    {
        await ConnectionPoolManager.ClearAllPoolsAsync();
    }

    /// <summary>
    /// Gets statistics for the connection pool.
    /// </summary>
    /// <returns>Pool statistics, or null if pooling is disabled.</returns>
    public PoolStatistics? GetPoolStatistics()
    {
        if (string.IsNullOrEmpty(_connectionString))
            return null;

        var builder = new DataWarehouseConnectionStringBuilder(_connectionString);
        if (!builder.Pooling)
            return null;

        var pool = ConnectionPoolManager.GetPool(_connectionString);
        return pool.Statistics;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            Close();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await CloseAsync();
        _disposed = true;
        await base.DisposeAsync();
    }

    private async Task<PooledConnection> CreateDirectConnectionAsync(DataWarehouseConnectionStringBuilder builder, CancellationToken cancellationToken)
    {
        var client = new System.Net.Sockets.TcpClient();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(builder.Timeout));

            await client.ConnectAsync(builder.Server, builder.Port, cts.Token);

            var connection = new PooledConnection(client, builder);
            await connection.InitializeAsync(cancellationToken);
            return connection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new DataWarehouseProviderException(
                DataWarehouseProviderException.ProviderErrorCode.OperationCancelled,
                "Connection attempt was cancelled.");
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or TimeoutException)
        {
            client.Dispose();
            throw new DataWarehouseConnectionException(
                $"Failed to connect to {builder.Server}:{builder.Port}. {ex.Message}",
                ex,
                builder.ToSanitizedString());
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
