using System.Collections.Concurrent;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Infrastructure;

/// <summary>
/// Connection pool configuration settings.
/// </summary>
public sealed record ConnectionPoolOptions
{
    /// <summary>Minimum number of connections to maintain in the pool.</summary>
    public int MinPoolSize { get; init; } = 2;

    /// <summary>Maximum number of connections allowed in the pool.</summary>
    public int MaxPoolSize { get; init; } = 100;

    /// <summary>Connection timeout in milliseconds when acquiring from pool.</summary>
    public int AcquireTimeoutMs { get; init; } = 30000;

    /// <summary>Idle timeout in milliseconds before connection is removed.</summary>
    public int IdleTimeoutMs { get; init; } = 300000; // 5 minutes

    /// <summary>Connection lifetime in milliseconds before forced recycling.</summary>
    public int MaxLifetimeMs { get; init; } = 3600000; // 1 hour

    /// <summary>Interval in milliseconds for health checks.</summary>
    public int HealthCheckIntervalMs { get; init; } = 30000; // 30 seconds

    /// <summary>Whether to validate connections before returning from pool.</summary>
    public bool ValidateOnAcquire { get; init; } = true;

    /// <summary>Whether to validate connections when returning to pool.</summary>
    public bool ValidateOnRelease { get; init; } = false;

    /// <summary>Whether to enable connection warm-up on pool initialization.</summary>
    public bool EnableWarmUp { get; init; } = true;

    /// <summary>Number of connections to warm up on initialization.</summary>
    public int WarmUpCount { get; init; } = 5;
}

/// <summary>
/// Statistics for connection pool monitoring.
/// </summary>
public sealed record ConnectionPoolStatistics
{
    /// <summary>Current number of available connections in the pool.</summary>
    public int AvailableConnections { get; init; }

    /// <summary>Current number of connections in use.</summary>
    public int InUseConnections { get; init; }

    /// <summary>Total connections ever created.</summary>
    public long TotalConnectionsCreated { get; init; }

    /// <summary>Total connections ever destroyed.</summary>
    public long TotalConnectionsDestroyed { get; init; }

    /// <summary>Total successful acquisitions.</summary>
    public long TotalAcquisitions { get; init; }

    /// <summary>Total failed acquisitions (timeout).</summary>
    public long FailedAcquisitions { get; init; }

    /// <summary>Total connections recycled due to lifetime.</summary>
    public long ConnectionsRecycled { get; init; }

    /// <summary>Total connections removed due to idle timeout.</summary>
    public long IdleConnectionsRemoved { get; init; }

    /// <summary>Total failed health checks.</summary>
    public long FailedHealthChecks { get; init; }

    /// <summary>Average wait time for connection acquisition in milliseconds.</summary>
    public double AverageAcquireTimeMs { get; init; }

    /// <summary>Peak number of connections in use.</summary>
    public int PeakInUseConnections { get; init; }

    /// <summary>Pool creation time.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Last statistics update time.</summary>
    public DateTime LastUpdated { get; init; }
}

/// <summary>
/// Represents a pooled connection with metadata.
/// </summary>
internal sealed class PooledConnection<TConnection> where TConnection : IDatabaseProtocolStrategy, IDisposable
{
    public TConnection Connection { get; }
    public DateTime CreatedAt { get; }
    // P2-2308: volatile/Interlocked fields ensure visibility across the acquire/release path.
    // DateTime cannot be volatile; store ticks as a long (Interlocked-safe).
    private long _lastUsedAtTicks;
    private volatile int _useCount;
    private volatile bool _isInUse;

    public DateTime LastUsedAt => new DateTime(Interlocked.Read(ref _lastUsedAtTicks), DateTimeKind.Utc);
    public int UseCount => _useCount;
    public bool IsInUse => _isInUse;
    public string PoolKey { get; }

    public PooledConnection(TConnection connection, string poolKey)
    {
        Connection = connection;
        PoolKey = poolKey;
        CreatedAt = DateTime.UtcNow;
        Interlocked.Exchange(ref _lastUsedAtTicks, DateTime.UtcNow.Ticks);
        _useCount = 0;
        _isInUse = false;
    }

    public void MarkInUse()
    {
        _isInUse = true;
        Interlocked.Exchange(ref _lastUsedAtTicks, DateTime.UtcNow.Ticks);
        Interlocked.Increment(ref _useCount);
    }

    public void MarkAvailable()
    {
        _isInUse = false;
        Interlocked.Exchange(ref _lastUsedAtTicks, DateTime.UtcNow.Ticks);
    }

    public bool IsExpired(int maxLifetimeMs) =>
        (DateTime.UtcNow - CreatedAt).TotalMilliseconds > maxLifetimeMs;

    public bool IsIdle(int idleTimeoutMs) =>
        !IsInUse && (DateTime.UtcNow - LastUsedAt).TotalMilliseconds > idleTimeoutMs;
}

/// <summary>
/// High-performance connection pool manager for database protocol strategies.
/// Supports multiple pools keyed by connection string, automatic health checks,
/// connection recycling, and comprehensive statistics.
/// </summary>
public sealed class ConnectionPoolManager<TConnection> : IDisposable, IAsyncDisposable
    where TConnection : IDatabaseProtocolStrategy, IDisposable
{
    private readonly BoundedDictionary<string, ConnectionPool> _pools = new BoundedDictionary<string, ConnectionPool>(1000);
    private readonly ConnectionPoolOptions _defaultOptions;
    private readonly Func<ConnectionParameters, Task<TConnection>> _connectionFactory;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _maintenanceTask;
    private int _disposed; // 0 = not disposed; 1 = disposed; Interlocked for atomic check-and-set

    // Statistics
    private long _totalConnectionsCreated;
    private long _totalConnectionsDestroyed;
    private long _totalAcquisitions;
    private long _failedAcquisitions;
    private long _connectionsRecycled;
    private long _idleConnectionsRemoved;
    private long _failedHealthChecks;
    private long _totalAcquireTimeMs;
    private int _peakInUseConnections;
    private readonly DateTime _createdAt;

    /// <summary>
    /// Creates a new connection pool manager.
    /// </summary>
    /// <param name="connectionFactory">Factory function to create new connections.</param>
    /// <param name="defaultOptions">Default pool options.</param>
    public ConnectionPoolManager(
        Func<ConnectionParameters, Task<TConnection>> connectionFactory,
        ConnectionPoolOptions? defaultOptions = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _defaultOptions = defaultOptions ?? new ConnectionPoolOptions();
        _createdAt = DateTime.UtcNow;

        // Start background maintenance task
        _maintenanceTask = Task.Run(RunMaintenanceLoopAsync);
    }

    /// <summary>
    /// Gets or creates a pool for the given connection parameters.
    /// </summary>
    public ConnectionPool GetOrCreatePool(ConnectionParameters parameters, ConnectionPoolOptions? options = null)
    {
        var key = GeneratePoolKey(parameters);
        return _pools.GetOrAdd(key, _ => new ConnectionPool(
            key,
            parameters,
            options ?? _defaultOptions,
            _connectionFactory,
            this));
    }

    /// <summary>
    /// Acquires a connection from the appropriate pool.
    /// </summary>
    public async Task<TConnection> AcquireAsync(
        ConnectionParameters parameters,
        CancellationToken ct = default)
    {
        var pool = GetOrCreatePool(parameters);
        var startTime = DateTime.UtcNow;

        try
        {
            var connection = await pool.AcquireAsync(ct);
            Interlocked.Increment(ref _totalAcquisitions);
            Interlocked.Add(ref _totalAcquireTimeMs, (long)(DateTime.UtcNow - startTime).TotalMilliseconds);

            UpdatePeakInUse();
            return connection;
        }
        catch
        {
            Interlocked.Increment(ref _failedAcquisitions);
            throw;
        }
    }

    /// <summary>
    /// Releases a connection back to its pool.
    /// </summary>
    public async Task ReleaseAsync(TConnection connection, CancellationToken ct = default)
    {
        foreach (var pool in _pools.Values)
        {
            if (await pool.TryReleaseAsync(connection, ct))
            {
                return;
            }
        }

        // Connection not found in any pool - just dispose it
        connection.Dispose();
    }

    /// <summary>
    /// Gets aggregate statistics across all pools.
    /// </summary>
    public ConnectionPoolStatistics GetStatistics()
    {
        var available = 0;
        var inUse = 0;

        foreach (var pool in _pools.Values)
        {
            var stats = pool.GetStatistics();
            available += stats.AvailableConnections;
            inUse += stats.InUseConnections;
        }

        var totalAcquisitions = Interlocked.Read(ref _totalAcquisitions);

        return new ConnectionPoolStatistics
        {
            AvailableConnections = available,
            InUseConnections = inUse,
            TotalConnectionsCreated = Interlocked.Read(ref _totalConnectionsCreated),
            TotalConnectionsDestroyed = Interlocked.Read(ref _totalConnectionsDestroyed),
            TotalAcquisitions = totalAcquisitions,
            FailedAcquisitions = Interlocked.Read(ref _failedAcquisitions),
            ConnectionsRecycled = Interlocked.Read(ref _connectionsRecycled),
            IdleConnectionsRemoved = Interlocked.Read(ref _idleConnectionsRemoved),
            FailedHealthChecks = Interlocked.Read(ref _failedHealthChecks),
            AverageAcquireTimeMs = totalAcquisitions > 0
                ? (double)Interlocked.Read(ref _totalAcquireTimeMs) / totalAcquisitions
                : 0,
            PeakInUseConnections = _peakInUseConnections,
            CreatedAt = _createdAt,
            LastUpdated = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Clears all pools and disposes all connections.
    /// </summary>
    public async Task ClearAllPoolsAsync(CancellationToken ct = default)
    {
        foreach (var pool in _pools.Values)
        {
            await pool.ClearAsync(ct);
        }
        _pools.Clear();
    }

    private static string GeneratePoolKey(ConnectionParameters parameters)
    {
        return $"{parameters.Host}:{parameters.Port ?? 0}:{parameters.Database}:{parameters.Username}:{parameters.UseSsl}";
    }

    private void UpdatePeakInUse()
    {
        var current = _pools.Values.Sum(p => p.GetStatistics().InUseConnections);
        int peak;
        do
        {
            peak = _peakInUseConnections;
            if (current <= peak) break;
        } while (Interlocked.CompareExchange(ref _peakInUseConnections, current, peak) != peak);
    }

    private async Task RunMaintenanceLoopAsync()
    {
        while (!_shutdownCts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_defaultOptions.HealthCheckIntervalMs, _shutdownCts.Token);

                foreach (var pool in _pools.Values)
                {
                    await pool.PerformMaintenanceAsync(_shutdownCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {

                // Continue on error
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }
    }

    internal void OnConnectionCreated() => Interlocked.Increment(ref _totalConnectionsCreated);
    internal void OnConnectionDestroyed() => Interlocked.Increment(ref _totalConnectionsDestroyed);
    internal void OnConnectionRecycled() => Interlocked.Increment(ref _connectionsRecycled);
    internal void OnIdleConnectionRemoved() => Interlocked.Increment(ref _idleConnectionsRemoved);
    internal void OnHealthCheckFailed() => Interlocked.Increment(ref _failedHealthChecks);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _shutdownCts.Cancel();
        try { _maintenanceTask.Wait(TimeSpan.FromSeconds(5)); } catch { /* Best-effort task wait */ }
        _shutdownCts.Dispose();

        foreach (var pool in _pools.Values)
        {
            pool.Dispose();
        }
        _pools.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _shutdownCts.Cancel();
        try { await _maintenanceTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* Best-effort task wait */ }
        _shutdownCts.Dispose();

        foreach (var pool in _pools.Values)
        {
            await pool.DisposeAsync();
        }
        _pools.Clear();
    }

    /// <summary>
    /// Individual connection pool for a specific connection key.
    /// </summary>
    public sealed class ConnectionPool : IDisposable, IAsyncDisposable
    {
        private readonly string _key;
        private readonly ConnectionParameters _parameters;
        private readonly ConnectionPoolOptions _options;
        private readonly Func<ConnectionParameters, Task<TConnection>> _connectionFactory;
        private readonly ConnectionPoolManager<TConnection> _manager;
        private readonly ConcurrentQueue<PooledConnection<TConnection>> _available = new();
        private readonly BoundedDictionary<TConnection, PooledConnection<TConnection>> _inUse = new BoundedDictionary<TConnection, PooledConnection<TConnection>>(1000);
        private readonly SemaphoreSlim _acquireSemaphore;
        private readonly SemaphoreSlim _createLock = new(1, 1);
        private int _totalCreated;
        // P2-2700: use int with Interlocked for atomic check-and-set; 0=not disposed, 1=disposed
        private int _disposed;

        internal ConnectionPool(
            string key,
            ConnectionParameters parameters,
            ConnectionPoolOptions options,
            Func<ConnectionParameters, Task<TConnection>> connectionFactory,
            ConnectionPoolManager<TConnection> manager)
        {
            _key = key;
            _parameters = parameters;
            _options = options;
            _connectionFactory = connectionFactory;
            _manager = manager;
            _acquireSemaphore = new SemaphoreSlim(options.MaxPoolSize, options.MaxPoolSize);

            if (options.EnableWarmUp)
            {
                _ = WarmUpAsync();
            }
        }

        private async Task WarmUpAsync()
        {
            var count = Math.Min(_options.WarmUpCount, _options.MaxPoolSize);
            var tasks = new List<Task>();

            for (int i = 0; i < count; i++)
            {
                tasks.Add(CreateAndPoolConnectionAsync());
            }

            await Task.WhenAll(tasks);
        }

        private async Task CreateAndPoolConnectionAsync()
        {
            try
            {
                var connection = await _connectionFactory(_parameters);
                await connection.ConnectAsync(_parameters);

                var pooled = new PooledConnection<TConnection>(connection, _key);
                _available.Enqueue(pooled);
                Interlocked.Increment(ref _totalCreated);
                _manager.OnConnectionCreated();
            }
            catch
            {

                // Ignore warm-up failures
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }

        public async Task<TConnection> AcquireAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            // Wait for slot in pool
            if (!await _acquireSemaphore.WaitAsync(_options.AcquireTimeoutMs, ct))
            {
                throw new TimeoutException($"Timeout acquiring connection from pool '{_key}'");
            }

            try
            {
                // Try to get an existing connection
                while (_available.TryDequeue(out var pooled))
                {
                    // Check if expired
                    if (pooled.IsExpired(_options.MaxLifetimeMs))
                    {
                        await DisposeConnectionAsync(pooled, true);
                        _manager.OnConnectionRecycled();
                        continue;
                    }

                    // Validate if required
                    if (_options.ValidateOnAcquire)
                    {
                        if (!await pooled.Connection.PingAsync(ct))
                        {
                            await DisposeConnectionAsync(pooled, false);
                            _manager.OnHealthCheckFailed();
                            continue;
                        }
                    }

                    // Check connection state
                    if (pooled.Connection.ConnectionState != ProtocolConnectionState.Ready)
                    {
                        await DisposeConnectionAsync(pooled, false);
                        continue;
                    }

                    pooled.MarkInUse();
                    _inUse[pooled.Connection] = pooled;
                    return pooled.Connection;
                }

                // Create new connection
                return await CreateNewConnectionAsync(ct);
            }
            catch
            {
                _acquireSemaphore.Release();
                throw;
            }
        }

        private async Task<TConnection> CreateNewConnectionAsync(CancellationToken ct)
        {
            await _createLock.WaitAsync(ct);
            try
            {
                var connection = await _connectionFactory(_parameters);
                await connection.ConnectAsync(_parameters, ct);

                var pooled = new PooledConnection<TConnection>(connection, _key);
                pooled.MarkInUse();
                _inUse[connection] = pooled;
                Interlocked.Increment(ref _totalCreated);
                _manager.OnConnectionCreated();

                return connection;
            }
            finally
            {
                _createLock.Release();
            }
        }

        public async Task<bool> TryReleaseAsync(TConnection connection, CancellationToken ct = default)
        {
            if (!_inUse.TryRemove(connection, out var pooled))
            {
                return false;
            }

            try
            {
                // Check if should be recycled
                if (pooled.IsExpired(_options.MaxLifetimeMs))
                {
                    await DisposeConnectionAsync(pooled, true);
                    _manager.OnConnectionRecycled();
                    return true;
                }

                // Validate if required
                if (_options.ValidateOnRelease)
                {
                    if (!await connection.PingAsync(ct))
                    {
                        await DisposeConnectionAsync(pooled, false);
                        _manager.OnHealthCheckFailed();
                        return true;
                    }
                }

                // Return to pool
                pooled.MarkAvailable();
                _available.Enqueue(pooled);
            }
            finally
            {
                _acquireSemaphore.Release();
            }

            return true;
        }

        public async Task PerformMaintenanceAsync(CancellationToken ct)
        {
            // Remove idle connections (keep minimum)
            var available = _available.ToArray();
            var currentCount = available.Length + _inUse.Count;

            foreach (var pooled in available)
            {
                if (currentCount <= _options.MinPoolSize)
                    break;

                if (pooled.IsIdle(_options.IdleTimeoutMs))
                {
                    // Try to remove from queue (best effort)
                    if (_available.TryDequeue(out var removed) && removed == pooled)
                    {
                        await DisposeConnectionAsync(pooled, false);
                        _manager.OnIdleConnectionRemoved();
                        currentCount--;
                    }
                    else if (removed != null)
                    {
                        // Put it back if we got the wrong one
                        _available.Enqueue(removed);
                    }
                }
            }

            // Ensure minimum connections
            while (currentCount < _options.MinPoolSize && !ct.IsCancellationRequested)
            {
                try
                {
                    await CreateAndPoolConnectionAsync();
                    currentCount++;
                }
                catch
                {
                    break;
                }
            }
        }

        public ConnectionPoolStatistics GetStatistics()
        {
            return new ConnectionPoolStatistics
            {
                AvailableConnections = _available.Count,
                InUseConnections = _inUse.Count,
                TotalConnectionsCreated = _totalCreated,
                LastUpdated = DateTime.UtcNow
            };
        }

        public async Task ClearAsync(CancellationToken ct = default)
        {
            while (_available.TryDequeue(out var pooled))
            {
                await DisposeConnectionAsync(pooled, false);
            }

            foreach (var kvp in _inUse)
            {
                await DisposeConnectionAsync(kvp.Value, false);
            }
            _inUse.Clear();
        }

        private async Task DisposeConnectionAsync(PooledConnection<TConnection> pooled, bool isRecycle)
        {
            try
            {
                await pooled.Connection.DisconnectAsync();
                pooled.Connection.Dispose();
            }
            catch
            {

                // Ignore disposal errors
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }

            _manager.OnConnectionDestroyed();
        }

        public void Dispose()
        {
            // P2-2700: atomic check-and-set prevents double-dispose from concurrent calls
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            // Dispose connections synchronously without blocking on DisposeAsync to avoid
            // deadlocks in ASP.NET Core synchronization contexts (finding 2691).
            while (_available.TryDequeue(out var pooled))
            {
                try { pooled.Connection.Dispose(); } catch { /* Best-effort */ }
                _manager.OnConnectionDestroyed();
            }
            foreach (var kvp in _inUse)
            {
                try { kvp.Value.Connection.Dispose(); } catch { /* Best-effort */ }
                _manager.OnConnectionDestroyed();
            }
            _inUse.Clear();

            _acquireSemaphore.Dispose();
            _createLock.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            // P2-2700: atomic check-and-set prevents double-dispose from concurrent calls
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            await ClearAsync();
            _acquireSemaphore.Dispose();
            _createLock.Dispose();
        }
    }
}

/// <summary>
/// Extension methods for connection pooling integration.
/// </summary>
public static class ConnectionPoolExtensions
{
    /// <summary>
    /// Creates a pooled connection scope that automatically returns the connection when disposed.
    /// </summary>
    public static async Task<PooledConnectionScope<TConnection>> AcquireScopedAsync<TConnection>(
        this ConnectionPoolManager<TConnection> pool,
        ConnectionParameters parameters,
        CancellationToken ct = default)
        where TConnection : IDatabaseProtocolStrategy, IDisposable
    {
        var connection = await pool.AcquireAsync(parameters, ct);
        return new PooledConnectionScope<TConnection>(pool, connection);
    }
}

/// <summary>
/// Scoped connection that automatically returns to pool on disposal.
/// </summary>
public readonly struct PooledConnectionScope<TConnection> : IAsyncDisposable
    where TConnection : IDatabaseProtocolStrategy, IDisposable
{
    private readonly ConnectionPoolManager<TConnection> _pool;

    /// <summary>The pooled connection.</summary>
    public TConnection Connection { get; }

    internal PooledConnectionScope(ConnectionPoolManager<TConnection> pool, TConnection connection)
    {
        _pool = pool;
        Connection = connection;
    }

    /// <summary>Releases the connection back to the pool.</summary>
    public async ValueTask DisposeAsync()
    {
        await _pool.ReleaseAsync(Connection);
    }
}
