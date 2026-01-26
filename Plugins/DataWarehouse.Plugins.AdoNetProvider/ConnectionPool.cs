using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;

namespace DataWarehouse.Plugins.AdoNetProvider;

/// <summary>
/// Manages a pool of connections to the DataWarehouse server.
/// Provides thread-safe connection acquisition and release with configurable pool size.
/// Implements automatic connection validation and cleanup of stale connections.
/// </summary>
/// <remarks>
/// Features:
/// - Configurable minimum and maximum pool size
/// - Automatic connection validation on acquire
/// - Background cleanup of idle connections
/// - Connection lifetime management
/// - Thread-safe operations using lock-free algorithms where possible
/// </remarks>
internal sealed class ConnectionPool : IDisposable
{
    private readonly ConcurrentBag<PooledConnection> _available;
    private readonly ConcurrentDictionary<Guid, PooledConnection> _inUse;
    private readonly DataWarehouseConnectionStringBuilder _connectionStringBuilder;
    private readonly SemaphoreSlim _poolSemaphore;
    private readonly Timer _cleanupTimer;
    private readonly object _statsLock = new();
    private readonly CancellationTokenSource _disposeCts;
    private volatile bool _disposed;
    private int _totalCreated;
    private int _totalDestroyed;
    private long _totalAcquired;
    private long _totalReleased;
    private long _totalWaitTime;

    /// <summary>
    /// Gets the connection string used by this pool.
    /// </summary>
    public string ConnectionString => _connectionStringBuilder.ConnectionString;

    /// <summary>
    /// Gets the maximum pool size.
    /// </summary>
    public int MaxPoolSize { get; }

    /// <summary>
    /// Gets the minimum pool size.
    /// </summary>
    public int MinPoolSize { get; }

    /// <summary>
    /// Gets the connection timeout in seconds.
    /// </summary>
    public int ConnectionTimeout { get; }

    /// <summary>
    /// Gets the current number of connections in the pool.
    /// </summary>
    public int CurrentPoolSize => _available.Count + _inUse.Count;

    /// <summary>
    /// Gets the number of available connections.
    /// </summary>
    public int AvailableCount => _available.Count;

    /// <summary>
    /// Gets the number of connections in use.
    /// </summary>
    public int InUseCount => _inUse.Count;

    /// <summary>
    /// Gets pool statistics.
    /// </summary>
    public PoolStatistics Statistics
    {
        get
        {
            lock (_statsLock)
            {
                return new PoolStatistics
                {
                    TotalCreated = _totalCreated,
                    TotalDestroyed = _totalDestroyed,
                    TotalAcquired = _totalAcquired,
                    TotalReleased = _totalReleased,
                    AverageWaitTimeMs = _totalAcquired > 0 ? _totalWaitTime / _totalAcquired : 0,
                    CurrentPoolSize = CurrentPoolSize,
                    AvailableCount = AvailableCount,
                    InUseCount = InUseCount
                };
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the ConnectionPool class.
    /// </summary>
    /// <param name="connectionString">The connection string to use.</param>
    /// <exception cref="ArgumentException">Thrown when the connection string is invalid.</exception>
    public ConnectionPool(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        _connectionStringBuilder = new DataWarehouseConnectionStringBuilder(connectionString);
        MaxPoolSize = _connectionStringBuilder.PoolSize;
        MinPoolSize = _connectionStringBuilder.MinPoolSize;
        ConnectionTimeout = _connectionStringBuilder.Timeout;

        _available = new ConcurrentBag<PooledConnection>();
        _inUse = new ConcurrentDictionary<Guid, PooledConnection>();
        _poolSemaphore = new SemaphoreSlim(MaxPoolSize, MaxPoolSize);
        _disposeCts = new CancellationTokenSource();

        // Start cleanup timer (runs every 30 seconds)
        _cleanupTimer = new Timer(CleanupCallback, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Acquires a connection from the pool.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A pooled connection.</returns>
    /// <exception cref="DataWarehouseProviderException">Thrown when connection acquisition fails.</exception>
    public async Task<PooledConnection> AcquireAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var stopwatch = Stopwatch.StartNew();

        // Wait for a slot in the pool
        var timeout = TimeSpan.FromSeconds(ConnectionTimeout);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);

        bool acquired;
        try
        {
            acquired = await _poolSemaphore.WaitAsync(timeout, cts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new DataWarehouseProviderException(
                DataWarehouseProviderException.ProviderErrorCode.OperationCancelled,
                "Connection acquisition was cancelled.");
        }

        if (!acquired)
        {
            throw new DataWarehouseProviderException(
                DataWarehouseProviderException.ProviderErrorCode.PoolExhausted,
                $"Unable to acquire a connection from the pool within {ConnectionTimeout} seconds. Pool size: {MaxPoolSize}, In use: {InUseCount}");
        }

        try
        {
            // Try to get an existing connection
            while (_available.TryTake(out var existing))
            {
                if (existing.IsValid && !existing.IsExpired)
                {
                    existing.MarkAcquired();
                    _inUse[existing.Id] = existing;
                    RecordAcquisition(stopwatch.ElapsedMilliseconds);
                    return existing;
                }

                // Connection is invalid or expired, destroy it
                await DestroyConnectionAsync(existing);
            }

            // Create a new connection
            var connection = await CreateConnectionAsync(cancellationToken);
            connection.MarkAcquired();
            _inUse[connection.Id] = connection;
            RecordAcquisition(stopwatch.ElapsedMilliseconds);
            return connection;
        }
        catch
        {
            // Release the semaphore slot if we failed
            _poolSemaphore.Release();
            throw;
        }
    }

    /// <summary>
    /// Releases a connection back to the pool.
    /// </summary>
    /// <param name="connection">The connection to release.</param>
    public async Task ReleaseAsync(PooledConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!_inUse.TryRemove(connection.Id, out _))
        {
            // Connection not found in in-use list, it may have been released already
            return;
        }

        try
        {
            if (_disposed)
            {
                await DestroyConnectionAsync(connection);
                return;
            }

            // Check if connection is still valid
            if (connection.IsValid && !connection.IsExpired)
            {
                connection.MarkReleased();
                _available.Add(connection);
            }
            else
            {
                await DestroyConnectionAsync(connection);
            }

            RecordRelease();
        }
        finally
        {
            _poolSemaphore.Release();
        }
    }

    /// <summary>
    /// Clears all connections from the pool.
    /// </summary>
    public async Task ClearAsync()
    {
        while (_available.TryTake(out var connection))
        {
            await DestroyConnectionAsync(connection);
        }

        foreach (var kvp in _inUse)
        {
            if (_inUse.TryRemove(kvp.Key, out var connection))
            {
                await DestroyConnectionAsync(connection);
            }
        }
    }

    /// <summary>
    /// Disposes the connection pool and all connections.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _disposeCts.Cancel();
        _cleanupTimer.Dispose();

        // Destroy all connections
        while (_available.TryTake(out var connection))
        {
            connection.Dispose();
        }

        foreach (var kvp in _inUse)
        {
            kvp.Value.Dispose();
        }
        _inUse.Clear();

        _poolSemaphore.Dispose();
        _disposeCts.Dispose();
    }

    private async Task<PooledConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var server = _connectionStringBuilder.Server;
        var port = _connectionStringBuilder.Port;

        var client = new TcpClient();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(ConnectionTimeout));

            await client.ConnectAsync(server, port, cts.Token);

            var connection = new PooledConnection(client, _connectionStringBuilder);
            await connection.InitializeAsync(cancellationToken);

            Interlocked.Increment(ref _totalCreated);
            return connection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new DataWarehouseProviderException(
                DataWarehouseProviderException.ProviderErrorCode.OperationCancelled,
                "Connection creation was cancelled.");
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            client.Dispose();
            throw new DataWarehouseConnectionException(
                $"Failed to connect to {server}:{port}. {ex.Message}",
                ex,
                _connectionStringBuilder.ToSanitizedString());
        }
    }

    private async Task DestroyConnectionAsync(PooledConnection connection)
    {
        try
        {
            await connection.CloseInternalAsync();
        }
        catch
        {
            // Ignore errors during cleanup
        }
        finally
        {
            connection.Dispose();
            Interlocked.Increment(ref _totalDestroyed);
        }
    }

    private void CleanupCallback(object? state)
    {
        if (_disposed)
            return;

        try
        {
            // Remove expired connections from available pool
            var toRemove = new List<PooledConnection>();
            var tempBag = new ConcurrentBag<PooledConnection>();

            while (_available.TryTake(out var connection))
            {
                if (connection.IsValid && !connection.IsExpired)
                {
                    tempBag.Add(connection);
                }
                else
                {
                    toRemove.Add(connection);
                }
            }

            // Put valid connections back
            foreach (var connection in tempBag)
            {
                _available.Add(connection);
            }

            // Destroy invalid connections
            foreach (var connection in toRemove)
            {
                _ = DestroyConnectionAsync(connection);
            }
        }
        catch
        {
            // Ignore errors in cleanup
        }
    }

    private void RecordAcquisition(long waitTimeMs)
    {
        lock (_statsLock)
        {
            _totalAcquired++;
            _totalWaitTime += waitTimeMs;
        }
    }

    private void RecordRelease()
    {
        Interlocked.Increment(ref _totalReleased);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ConnectionPool));
        }
    }
}

/// <summary>
/// Represents a pooled connection to the DataWarehouse server.
/// </summary>
internal sealed class PooledConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly DataWarehouseConnectionStringBuilder _connectionStringBuilder;
    private readonly DateTime _createdAt;
    private readonly TimeSpan _maxLifetime = TimeSpan.FromMinutes(30);
    private readonly object _lock = new();
    private NetworkStream? _stream;
    private DateTime _lastUsedAt;
    private bool _disposed;

    /// <summary>
    /// Gets the unique identifier for this connection.
    /// </summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// Gets whether the connection is valid.
    /// </summary>
    public bool IsValid
    {
        get
        {
            lock (_lock)
            {
                if (_disposed)
                    return false;

                try
                {
                    return _client.Connected && _stream != null;
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    /// <summary>
    /// Gets whether the connection has expired.
    /// </summary>
    public bool IsExpired => DateTime.UtcNow - _createdAt > _maxLifetime;

    /// <summary>
    /// Gets the network stream for this connection.
    /// </summary>
    public NetworkStream Stream
    {
        get
        {
            lock (_lock)
            {
                if (_disposed || _stream == null)
                    throw new ObjectDisposedException(nameof(PooledConnection));
                return _stream;
            }
        }
    }

    /// <summary>
    /// Gets the connection string builder.
    /// </summary>
    public DataWarehouseConnectionStringBuilder ConnectionStringBuilder => _connectionStringBuilder;

    /// <summary>
    /// Initializes a new instance of the PooledConnection class.
    /// </summary>
    /// <param name="client">The TCP client.</param>
    /// <param name="connectionStringBuilder">The connection string builder.</param>
    public PooledConnection(TcpClient client, DataWarehouseConnectionStringBuilder connectionStringBuilder)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _connectionStringBuilder = connectionStringBuilder ?? throw new ArgumentNullException(nameof(connectionStringBuilder));
        _createdAt = DateTime.UtcNow;
        _lastUsedAt = _createdAt;
    }

    /// <summary>
    /// Initializes the connection with the server.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PooledConnection));

            _stream = _client.GetStream();
        }

        // Perform PostgreSQL protocol handshake
        await PerformHandshakeAsync(cancellationToken);
    }

    /// <summary>
    /// Marks the connection as acquired.
    /// </summary>
    public void MarkAcquired()
    {
        lock (_lock)
        {
            _lastUsedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Marks the connection as released.
    /// </summary>
    public void MarkReleased()
    {
        lock (_lock)
        {
            _lastUsedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Closes the internal connection.
    /// </summary>
    public async Task CloseInternalAsync()
    {
        if (_stream != null)
        {
            try
            {
                // Send Terminate message
                var message = new byte[] { (byte)'X', 0, 0, 0, 4 };
                await _stream.WriteAsync(message);
            }
            catch
            {
                // Ignore errors during termination
            }
        }
    }

    /// <summary>
    /// Disposes the pooled connection.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _stream?.Dispose();
            _client.Dispose();
        }
    }

    private async Task PerformHandshakeAsync(CancellationToken cancellationToken)
    {
        if (_stream == null)
            throw new InvalidOperationException("Stream not initialized.");

        // Build startup message
        var database = _connectionStringBuilder.Database;
        var user = _connectionStringBuilder.UserId ?? "datawarehouse";
        var appName = _connectionStringBuilder.ApplicationName ?? "DataWarehouse.AdoNetProvider";

        var parameters = new Dictionary<string, string>
        {
            { "user", user },
            { "database", database },
            { "application_name", appName },
            { "client_encoding", "UTF8" }
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Protocol version 3.0
        writer.Write(ReverseBytes(196608));

        foreach (var kvp in parameters)
        {
            writer.Write(System.Text.Encoding.UTF8.GetBytes(kvp.Key));
            writer.Write((byte)0);
            writer.Write(System.Text.Encoding.UTF8.GetBytes(kvp.Value));
            writer.Write((byte)0);
        }
        writer.Write((byte)0); // Terminator

        var body = ms.ToArray();
        var length = body.Length + 4;
        var message = new byte[length];
        BitConverter.GetBytes(length).Reverse().ToArray().CopyTo(message, 0);
        body.CopyTo(message, 4);

        await _stream.WriteAsync(message, cancellationToken);

        // Read authentication response
        await ReadAuthResponseAsync(cancellationToken);
    }

    private async Task ReadAuthResponseAsync(CancellationToken cancellationToken)
    {
        if (_stream == null)
            return;

        var buffer = new byte[1];
        while (await _stream.ReadAsync(buffer, cancellationToken) > 0)
        {
            var messageType = (char)buffer[0];

            // Read length
            var lengthBytes = new byte[4];
            await ReadExactAsync(lengthBytes, cancellationToken);
            var length = BitConverter.ToInt32(lengthBytes.Reverse().ToArray(), 0) - 4;

            if (length > 0)
            {
                var body = new byte[length];
                await ReadExactAsync(body, cancellationToken);

                // Handle message types
                switch (messageType)
                {
                    case 'E': // ErrorResponse
                        var errorMsg = ParseErrorResponse(body);
                        throw new DataWarehouseConnectionException(
                            $"Server error: {errorMsg}",
                            _connectionStringBuilder.ToSanitizedString());

                    case 'R': // AuthenticationOk
                        var authType = BitConverter.ToInt32(body.Take(4).Reverse().ToArray(), 0);
                        if (authType != 0)
                        {
                            throw new DataWarehouseProviderException(
                                DataWarehouseProviderException.ProviderErrorCode.AuthenticationFailed,
                                $"Unsupported authentication type: {authType}");
                        }
                        break;

                    case 'Z': // ReadyForQuery
                        return; // Handshake complete

                    case 'S': // ParameterStatus
                    case 'K': // BackendKeyData
                        // Ignore these messages
                        break;

                    default:
                        // Unknown message, skip
                        break;
                }
            }
            else if (messageType == 'Z')
            {
                return; // ReadyForQuery with no body
            }
        }
    }

    private async Task ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        if (_stream == null)
            return;

        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
                throw new DataWarehouseConnectionException("Connection closed unexpectedly.");
            offset += read;
        }
    }

    private static string ParseErrorResponse(byte[] body)
    {
        var message = string.Empty;
        var i = 0;
        while (i < body.Length && body[i] != 0)
        {
            var fieldType = (char)body[i++];
            var end = Array.IndexOf(body, (byte)0, i);
            if (end < 0) end = body.Length;
            var value = System.Text.Encoding.UTF8.GetString(body, i, end - i);
            i = end + 1;

            if (fieldType == 'M')
                message = value;
        }
        return message;
    }

    private static int ReverseBytes(int value)
    {
        return BitConverter.ToInt32(BitConverter.GetBytes(value).Reverse().ToArray(), 0);
    }
}

/// <summary>
/// Statistics for a connection pool.
/// </summary>
public sealed class PoolStatistics
{
    /// <summary>
    /// Gets or sets the total number of connections created.
    /// </summary>
    public int TotalCreated { get; init; }

    /// <summary>
    /// Gets or sets the total number of connections destroyed.
    /// </summary>
    public int TotalDestroyed { get; init; }

    /// <summary>
    /// Gets or sets the total number of connection acquisitions.
    /// </summary>
    public long TotalAcquired { get; init; }

    /// <summary>
    /// Gets or sets the total number of connection releases.
    /// </summary>
    public long TotalReleased { get; init; }

    /// <summary>
    /// Gets or sets the average wait time in milliseconds.
    /// </summary>
    public long AverageWaitTimeMs { get; init; }

    /// <summary>
    /// Gets or sets the current pool size.
    /// </summary>
    public int CurrentPoolSize { get; init; }

    /// <summary>
    /// Gets or sets the number of available connections.
    /// </summary>
    public int AvailableCount { get; init; }

    /// <summary>
    /// Gets or sets the number of connections in use.
    /// </summary>
    public int InUseCount { get; init; }
}

/// <summary>
/// Manages connection pools for different connection strings.
/// </summary>
internal static class ConnectionPoolManager
{
    private static readonly ConcurrentDictionary<string, ConnectionPool> _pools = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    /// <summary>
    /// Gets or creates a connection pool for the specified connection string.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    /// <returns>The connection pool.</returns>
    public static ConnectionPool GetPool(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        return _pools.GetOrAdd(connectionString, cs =>
        {
            lock (_lock)
            {
                return _pools.GetOrAdd(cs, _ => new ConnectionPool(cs));
            }
        });
    }

    /// <summary>
    /// Clears all connection pools.
    /// </summary>
    public static async Task ClearAllPoolsAsync()
    {
        foreach (var pool in _pools.Values)
        {
            await pool.ClearAsync();
        }
    }

    /// <summary>
    /// Clears a specific connection pool.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    public static async Task ClearPoolAsync(string connectionString)
    {
        if (_pools.TryGetValue(connectionString, out var pool))
        {
            await pool.ClearAsync();
        }
    }
}
