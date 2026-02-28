using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Infrastructure;

#region Generic Storage Connection Registry

/// <summary>
/// Generic multi-instance connection registry for any storage/database plugin.
/// Enables a single plugin to manage multiple backend connections simultaneously.
/// Thread-safe with support for connection pooling, health monitoring, and role-based selection.
/// </summary>
/// <typeparam name="TConfig">Configuration type for the connection (e.g., S3Config, RelationalDbConfig).</typeparam>
public class StorageConnectionRegistry<TConfig> : IAsyncDisposable where TConfig : class
{
    private readonly BoundedDictionary<string, StorageConnectionInstance<TConfig>> _instances = new BoundedDictionary<string, StorageConnectionInstance<TConfig>>(1000);
    private readonly SemaphoreSlim _registryLock = new(1, 1);
    private readonly Func<TConfig, Task<object>>? _defaultConnectionFactory;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a new connection registry.
    /// </summary>
    /// <param name="defaultConnectionFactory">Optional default factory for creating connections.</param>
    public StorageConnectionRegistry(Func<TConfig, Task<object>>? defaultConnectionFactory = null)
    {
        _defaultConnectionFactory = defaultConnectionFactory;
    }

    /// <summary>
    /// Gets all registered instance IDs.
    /// </summary>
    public IEnumerable<string> InstanceIds => _instances.Keys;

    /// <summary>
    /// Gets the count of registered instances.
    /// </summary>
    public int Count => _instances.Count;

    /// <summary>
    /// Checks if an instance is registered.
    /// </summary>
    public bool Contains(string instanceId) => _instances.ContainsKey(instanceId);

    /// <summary>
    /// Registers a new connection instance with a custom factory.
    /// </summary>
    /// <param name="instanceId">Unique identifier for this instance.</param>
    /// <param name="config">Connection configuration.</param>
    /// <param name="connectionFactory">Factory to create connections.</param>
    /// <param name="roles">Roles this instance serves.</param>
    /// <param name="priority">Priority for role selection (higher = preferred).</param>
    /// <returns>The registered instance.</returns>
    public async Task<StorageConnectionInstance<TConfig>> RegisterAsync(
        string instanceId,
        TConfig config,
        Func<TConfig, Task<object>> connectionFactory,
        StorageRole roles = StorageRole.Primary,
        int priority = 0)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("Instance ID cannot be empty.", nameof(instanceId));

        await _registryLock.WaitAsync();
        try
        {
            if (_instances.ContainsKey(instanceId))
                throw new InvalidOperationException($"Instance '{instanceId}' is already registered.");

            var instance = new StorageConnectionInstance<TConfig>(instanceId, config, connectionFactory)
            {
                Roles = roles,
                Priority = priority
            };
            _instances[instanceId] = instance;

            return instance;
        }
        finally
        {
            _registryLock.Release();
        }
    }

    /// <summary>
    /// Registers a new connection instance using the default factory.
    /// </summary>
    public async Task<StorageConnectionInstance<TConfig>> RegisterAsync(
        string instanceId,
        TConfig config,
        StorageRole roles = StorageRole.Primary,
        int priority = 0)
    {
        if (_defaultConnectionFactory == null)
            throw new InvalidOperationException("No default connection factory provided. Use the overload with a factory parameter.");

        return await RegisterAsync(instanceId, config, _defaultConnectionFactory, roles, priority);
    }

    /// <summary>
    /// Registers an instance synchronously (for simple registrations without async factory).
    /// </summary>
    public StorageConnectionInstance<TConfig> Register(
        string instanceId,
        TConfig config,
        StorageRole roles = StorageRole.Primary,
        int priority = 0)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("Instance ID cannot be empty.", nameof(instanceId));

        if (_instances.ContainsKey(instanceId))
            throw new InvalidOperationException($"Instance '{instanceId}' is already registered.");

        var factory = _defaultConnectionFactory ?? (c => Task.FromResult<object>(new { Config = c }));
        var instance = new StorageConnectionInstance<TConfig>(instanceId, config, factory)
        {
            Roles = roles,
            Priority = priority
        };
        _instances[instanceId] = instance;

        return instance;
    }

    /// <summary>
    /// Gets an existing instance by ID.
    /// </summary>
    public StorageConnectionInstance<TConfig>? Get(string instanceId)
    {
        ThrowIfDisposed();
        return _instances.TryGetValue(instanceId, out var instance) ? instance : null;
    }

    /// <summary>
    /// Gets an instance or throws if not found.
    /// </summary>
    public StorageConnectionInstance<TConfig> GetRequired(string instanceId)
    {
        return Get(instanceId)
            ?? throw new InvalidOperationException($"Connection instance '{instanceId}' not found.");
    }

    /// <summary>
    /// Gets or creates a default instance.
    /// </summary>
    public async Task<StorageConnectionInstance<TConfig>> GetOrCreateDefaultAsync(
        TConfig config,
        Func<TConfig, Task<object>>? connectionFactory = null)
    {
        const string defaultId = "__default__";
        var existing = Get(defaultId);
        if (existing != null) return existing;

        var factory = connectionFactory ?? _defaultConnectionFactory
            ?? throw new InvalidOperationException("No connection factory provided.");

        return await RegisterAsync(defaultId, config, factory);
    }

    /// <summary>
    /// Gets all registered instances.
    /// </summary>
    public IEnumerable<StorageConnectionInstance<TConfig>> GetAll()
    {
        ThrowIfDisposed();
        return _instances.Values.ToArray();
    }

    /// <summary>
    /// Gets all instances matching a specific role.
    /// </summary>
    public IEnumerable<StorageConnectionInstance<TConfig>> GetByRole(StorageRole role)
    {
        ThrowIfDisposed();
        return _instances.Values.Where(i => i.Roles.HasFlag(role));
    }

    /// <summary>
    /// Gets the primary (highest priority) instance for a specific role.
    /// </summary>
    public StorageConnectionInstance<TConfig>? GetPrimaryForRole(StorageRole role)
    {
        return GetByRole(role)
            .Where(i => i.Health != InstanceHealthStatus.Unhealthy)
            .OrderByDescending(i => i.Priority)
            .ThenBy(i => i.InstanceId) // Deterministic ordering
            .FirstOrDefault();
    }

    /// <summary>
    /// Gets a healthy instance for a role, with fallback.
    /// </summary>
    public StorageConnectionInstance<TConfig>? GetHealthyForRole(StorageRole role)
    {
        return GetByRole(role)
            .Where(i => i.Health == InstanceHealthStatus.Healthy)
            .OrderByDescending(i => i.Priority)
            .FirstOrDefault()
            ?? GetByRole(role)
                .Where(i => i.Health == InstanceHealthStatus.Degraded)
                .OrderByDescending(i => i.Priority)
                .FirstOrDefault();
    }

    /// <summary>
    /// Unregisters and disposes an instance.
    /// </summary>
    public async Task UnregisterAsync(string instanceId)
    {
        ThrowIfDisposed();

        await _registryLock.WaitAsync();
        try
        {
            if (_instances.TryRemove(instanceId, out var instance))
            {
                await instance.DisposeAsync();
            }
        }
        finally
        {
            _registryLock.Release();
        }
    }

    /// <summary>
    /// Unregisters all instances.
    /// </summary>
    public async Task ClearAsync()
    {
        ThrowIfDisposed();

        await _registryLock.WaitAsync();
        try
        {
            foreach (var instance in _instances.Values)
            {
                await instance.DisposeAsync();
            }
            _instances.Clear();
        }
        finally
        {
            _registryLock.Release();
        }
    }

    /// <summary>
    /// Performs health checks on all instances.
    /// </summary>
    public async Task<Dictionary<string, InstanceHealthStatus>> CheckHealthAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var results = new Dictionary<string, InstanceHealthStatus>();

        foreach (var instance in _instances.Values)
        {
            try
            {
                await instance.CheckHealthAsync(ct);
                results[instance.InstanceId] = instance.Health;
            }
            catch
            {
                results[instance.InstanceId] = InstanceHealthStatus.Unhealthy;
            }
        }

        return results;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StorageConnectionRegistry<TConfig>));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var instance in _instances.Values)
        {
            await instance.DisposeAsync();
        }
        _instances.Clear();
        _registryLock.Dispose();
    }
}

#endregion

#region Connection Instance

/// <summary>
/// Represents a single storage/database connection instance.
/// Manages connection state, pooling, and health monitoring.
/// </summary>
/// <typeparam name="TConfig">Configuration type.</typeparam>
public sealed class StorageConnectionInstance<TConfig> : IAsyncDisposable where TConfig : class
{
    private readonly Func<TConfig, Task<object>> _connectionFactory;
    private readonly ConcurrentQueue<object> _connectionPool = new();
    private readonly SemaphoreSlim _poolSemaphore;
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private int _activeConnections;
    private volatile bool _disposed;
    private object? _primaryConnection;
    private Func<object, CancellationToken, Task<bool>>? _healthCheck;

    /// <summary>
    /// Unique identifier for this instance.
    /// </summary>
    public string InstanceId { get; }

    /// <summary>
    /// Connection configuration.
    /// </summary>
    public TConfig Config { get; }

    /// <summary>
    /// Roles this instance serves.
    /// </summary>
    public StorageRole Roles { get; set; } = StorageRole.Primary;

    /// <summary>
    /// Priority for role selection (higher = preferred).
    /// </summary>
    public int Priority { get; set; } = 0;

    /// <summary>
    /// Optional display name for this instance.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Optional tags for categorization.
    /// </summary>
    public HashSet<string> Tags { get; } = new();

    /// <summary>
    /// Maximum connections in the pool.
    /// </summary>
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>
    /// Whether any connection is currently active.
    /// </summary>
    public bool IsConnected => _activeConnections > 0 || _primaryConnection != null;

    /// <summary>
    /// Number of active (checked out) connections.
    /// </summary>
    public int ActiveConnections => _activeConnections;

    /// <summary>
    /// Number of pooled connections available.
    /// </summary>
    public int PooledConnections => _connectionPool.Count;

    /// <summary>
    /// Last successful operation timestamp.
    /// </summary>
    public DateTime? LastActivity { get; private set; }

    /// <summary>
    /// When the instance was created.
    /// </summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>
    /// Health status of this instance.
    /// </summary>
    public InstanceHealthStatus Health { get; private set; } = InstanceHealthStatus.Unknown;

    /// <summary>
    /// Last health check timestamp.
    /// </summary>
    public DateTime? LastHealthCheck { get; private set; }

    /// <summary>
    /// Last error message if unhealthy.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Connection statistics.
    /// </summary>
    public StorageConnectionStats Stats { get; } = new();

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public Dictionary<string, object> Metadata { get; } = new();

    /// <summary>
    /// Gets the primary connection object. Creates one if not exists.
    /// For pooled access, use GetConnectionAsync() instead.
    /// </summary>
    public object? Connection => _primaryConnection;

    /// <summary>
    /// Gets or creates the primary connection synchronously.
    /// Prefer GetConnectionAsync for async contexts.
    /// </summary>
    public async Task<object> GetOrCreateConnectionAsync(CancellationToken ct = default)
    {
        if (_primaryConnection != null) return _primaryConnection;

        await _createLock.WaitAsync(ct);
        try
        {
            _primaryConnection ??= await _connectionFactory(Config);
            return _primaryConnection;
        }
        finally
        {
            _createLock.Release();
        }
    }

    public StorageConnectionInstance(
        string instanceId,
        TConfig config,
        Func<TConfig, Task<object>> connectionFactory)
    {
        InstanceId = instanceId;
        Config = config;
        _connectionFactory = connectionFactory;
        _poolSemaphore = new SemaphoreSlim(MaxPoolSize, MaxPoolSize);
    }

    /// <summary>
    /// Sets a custom health check function.
    /// </summary>
    public void SetHealthCheck(Func<object, CancellationToken, Task<bool>> healthCheck)
    {
        _healthCheck = healthCheck;
    }

    /// <summary>
    /// Gets a connection from the pool or creates a new one.
    /// </summary>
    public async Task<PooledStorageConnection<TConfig>> GetConnectionAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        await _poolSemaphore.WaitAsync(ct);

        try
        {
            // Try to get from pool
            if (_connectionPool.TryDequeue(out var pooled))
            {
                Interlocked.Increment(ref _activeConnections);
                Stats.PoolHits++;
                return new PooledStorageConnection<TConfig>(pooled, this);
            }

            // Create new connection
            await _createLock.WaitAsync(ct);
            try
            {
                var connection = await _connectionFactory(Config);
                Interlocked.Increment(ref _activeConnections);
                Stats.ConnectionsCreated++;
                LastActivity = DateTime.UtcNow;
                Health = InstanceHealthStatus.Healthy;
                LastError = null;
                return new PooledStorageConnection<TConfig>(connection, this);
            }
            finally
            {
                _createLock.Release();
            }
        }
        catch (Exception ex)
        {
            _poolSemaphore.Release();
            Stats.ConnectionErrors++;
            Health = InstanceHealthStatus.Unhealthy;
            LastError = ex.Message;
            throw new StorageConnectionException($"Failed to get connection for instance '{InstanceId}'", ex);
        }
    }

    /// <summary>
    /// Gets or creates the primary (shared) connection.
    /// Use for single-connection backends or shared connection scenarios.
    /// </summary>
    public async Task<object> GetPrimaryConnectionAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (_primaryConnection != null) return _primaryConnection;

        await _createLock.WaitAsync(ct);
        try
        {
            if (_primaryConnection != null) return _primaryConnection;

            _primaryConnection = await _connectionFactory(Config);
            Interlocked.Increment(ref _activeConnections);
            Stats.ConnectionsCreated++;
            LastActivity = DateTime.UtcNow;
            Health = InstanceHealthStatus.Healthy;
            LastError = null;

            return _primaryConnection;
        }
        catch (Exception ex)
        {
            Stats.ConnectionErrors++;
            Health = InstanceHealthStatus.Unhealthy;
            LastError = ex.Message;
            throw new StorageConnectionException($"Failed to create primary connection for instance '{InstanceId}'", ex);
        }
        finally
        {
            _createLock.Release();
        }
    }

    /// <summary>
    /// Gets the primary connection as a specific type.
    /// </summary>
    public async Task<T> GetPrimaryConnectionAsync<T>(CancellationToken ct = default)
    {
        var conn = await GetPrimaryConnectionAsync(ct);
        return (T)conn;
    }

    /// <summary>
    /// Performs a health check on this instance.
    /// </summary>
    public async Task CheckHealthAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        LastHealthCheck = DateTime.UtcNow;

        try
        {
            if (_healthCheck != null && _primaryConnection != null)
            {
                var healthy = await _healthCheck(_primaryConnection, ct);
                Health = healthy ? InstanceHealthStatus.Healthy : InstanceHealthStatus.Unhealthy;
            }
            else if (_primaryConnection != null)
            {
                // Basic check - connection exists
                Health = InstanceHealthStatus.Healthy;
            }
            else
            {
                // Try to create a connection
                var conn = await _connectionFactory(Config);
                Health = InstanceHealthStatus.Healthy;

                // Add to pool if there's room
                if (_connectionPool.Count < MaxPoolSize)
                {
                    _connectionPool.Enqueue(conn);
                }
                else
                {
                    DisposeConnection(conn);
                }
            }
            LastError = null;
        }
        catch (Exception ex)
        {
            Health = InstanceHealthStatus.Unhealthy;
            LastError = ex.Message;
        }
    }

    /// <summary>
    /// Records activity timestamp.
    /// </summary>
    public void RecordActivity() => LastActivity = DateTime.UtcNow;

    /// <summary>
    /// Returns a connection to the pool.
    /// </summary>
    internal void ReturnConnection(object connection)
    {
        if (_disposed)
        {
            DisposeConnection(connection);
            return;
        }

        Interlocked.Decrement(ref _activeConnections);
        LastActivity = DateTime.UtcNow;

        // Return to pool if under limit
        if (_connectionPool.Count < MaxPoolSize)
        {
            _connectionPool.Enqueue(connection);
            Stats.PoolReturns++;
        }
        else
        {
            DisposeConnection(connection);
        }

        _poolSemaphore.Release();
    }

    private static void DisposeConnection(object connection)
    {
        if (connection is IAsyncDisposable asyncDisposable)
        {
            // Observe disposal failures to prevent silent resource leaks
            asyncDisposable.DisposeAsync().AsTask().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    System.Diagnostics.Debug.WriteLine($"Connection disposal failed: {t.Exception?.GetBaseException().Message}");
            }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
        else if (connection is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static async ValueTask DisposeConnectionAsync(object connection)
    {
        if (connection is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (connection is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StorageConnectionInstance<TConfig>));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Dispose primary connection
        if (_primaryConnection != null)
        {
            await DisposeConnectionAsync(_primaryConnection);
            _primaryConnection = null;
        }

        // Dispose all pooled connections
        while (_connectionPool.TryDequeue(out var connection))
        {
            await DisposeConnectionAsync(connection);
        }

        _poolSemaphore.Dispose();
        _createLock.Dispose();
    }
}

#endregion

#region Pooled Connection Wrapper

/// <summary>
/// Wraps a pooled connection for automatic return to the pool.
/// Use with 'await using' for automatic cleanup.
/// </summary>
public sealed class PooledStorageConnection<TConfig> : IAsyncDisposable where TConfig : class
{
    private readonly StorageConnectionInstance<TConfig> _instance;
    private bool _disposed;

    /// <summary>
    /// The underlying connection object.
    /// </summary>
    public object Connection { get; }

    internal PooledStorageConnection(object connection, StorageConnectionInstance<TConfig> instance)
    {
        Connection = connection;
        _instance = instance;
    }

    /// <summary>
    /// Gets the connection cast to a specific type.
    /// </summary>
    public T GetConnection<T>() => (T)Connection;

    /// <summary>
    /// Records activity on the connection.
    /// </summary>
    public void RecordActivity() => _instance.RecordActivity();

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _instance.ReturnConnection(Connection);
        return ValueTask.CompletedTask;
    }
}

#endregion

#region Supporting Types

/// <summary>
/// Health status of a connection instance.
/// </summary>
public enum InstanceHealthStatus
{
    /// <summary>Health status not yet determined.</summary>
    Unknown,

    /// <summary>Connection is healthy and responsive.</summary>
    Healthy,

    /// <summary>Connection is operational but experiencing issues.</summary>
    Degraded,

    /// <summary>Connection is not available.</summary>
    Unhealthy
}

/// <summary>
/// Statistics for a storage connection instance.
/// </summary>
public class StorageConnectionStats
{
    /// <summary>Total connections created.</summary>
    public long ConnectionsCreated { get; set; }

    /// <summary>Connection creation errors.</summary>
    public long ConnectionErrors { get; set; }

    /// <summary>Successful pool retrievals (cache hits).</summary>
    public long PoolHits { get; set; }

    /// <summary>Connections returned to pool.</summary>
    public long PoolReturns { get; set; }

    /// <summary>Operations executed.</summary>
    public long OperationsExecuted { get; set; }

    /// <summary>Operation errors.</summary>
    public long OperationErrors { get; set; }

    /// <summary>Total operation time in milliseconds.</summary>
    public double TotalOperationTimeMs { get; set; }

    /// <summary>Bytes written.</summary>
    public long BytesWritten { get; set; }

    /// <summary>Bytes read.</summary>
    public long BytesRead { get; set; }

    /// <summary>Pool hit ratio (0.0 to 1.0).</summary>
    public double PoolHitRatio => ConnectionsCreated > 0
        ? (double)PoolHits / (PoolHits + ConnectionsCreated)
        : 0;

    /// <summary>Average operation time in milliseconds.</summary>
    public double AverageOperationTimeMs => OperationsExecuted > 0
        ? TotalOperationTimeMs / OperationsExecuted
        : 0;

    /// <summary>Success rate (0.0 to 1.0).</summary>
    public double SuccessRate => OperationsExecuted > 0
        ? (double)(OperationsExecuted - OperationErrors) / OperationsExecuted
        : 0;

    /// <summary>
    /// Resets all statistics.
    /// </summary>
    public void Reset()
    {
        ConnectionsCreated = 0;
        ConnectionErrors = 0;
        PoolHits = 0;
        PoolReturns = 0;
        OperationsExecuted = 0;
        OperationErrors = 0;
        TotalOperationTimeMs = 0;
        BytesWritten = 0;
        BytesRead = 0;
    }
}

/// <summary>
/// Exception for storage connection errors.
/// </summary>
public class StorageConnectionException : Exception
{
    public StorageConnectionException(string message) : base(message) { }
    public StorageConnectionException(string message, Exception inner) : base(message, inner) { }
}

#endregion

#region Registry Builder

/// <summary>
/// Fluent builder for creating and configuring storage connection registries.
/// </summary>
public class StorageConnectionRegistryBuilder<TConfig> where TConfig : class
{
    private Func<TConfig, Task<object>>? _defaultFactory;
    private readonly List<(string id, TConfig config, StorageRole roles, int priority)> _instances = new();

    /// <summary>
    /// Sets the default connection factory.
    /// </summary>
    public StorageConnectionRegistryBuilder<TConfig> WithDefaultFactory(Func<TConfig, Task<object>> factory)
    {
        _defaultFactory = factory;
        return this;
    }

    /// <summary>
    /// Sets a synchronous default connection factory.
    /// </summary>
    public StorageConnectionRegistryBuilder<TConfig> WithDefaultFactory(Func<TConfig, object> factory)
    {
        _defaultFactory = config => Task.FromResult(factory(config));
        return this;
    }

    /// <summary>
    /// Adds an instance to be registered.
    /// </summary>
    public StorageConnectionRegistryBuilder<TConfig> AddInstance(
        string instanceId,
        TConfig config,
        StorageRole roles = StorageRole.Primary,
        int priority = 0)
    {
        _instances.Add((instanceId, config, roles, priority));
        return this;
    }

    /// <summary>
    /// Builds the registry and registers all instances.
    /// </summary>
    public async Task<StorageConnectionRegistry<TConfig>> BuildAsync()
    {
        var registry = new StorageConnectionRegistry<TConfig>(_defaultFactory);

        foreach (var (id, config, roles, priority) in _instances)
        {
            await registry.RegisterAsync(id, config, roles, priority);
        }

        return registry;
    }

    /// <summary>
    /// Builds the registry synchronously (for simple scenarios).
    /// </summary>
    public StorageConnectionRegistry<TConfig> Build()
    {
        var registry = new StorageConnectionRegistry<TConfig>(_defaultFactory);

        foreach (var (id, config, roles, priority) in _instances)
        {
            registry.Register(id, config, roles, priority);
        }

        return registry;
    }
}

/// <summary>
/// Extension methods for StorageConnectionRegistry.
/// </summary>
public static class StorageConnectionRegistryExtensions
{
    /// <summary>
    /// Creates a builder for a storage connection registry.
    /// </summary>
    public static StorageConnectionRegistryBuilder<TConfig> CreateBuilder<TConfig>() where TConfig : class
        => new();
}

#endregion
