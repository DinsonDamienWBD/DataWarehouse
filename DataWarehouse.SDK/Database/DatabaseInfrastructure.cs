using System.Collections.Concurrent;
using System.Data;
using System.Text.Json;

namespace DataWarehouse.SDK.Database;

#region Connection Registry (Multi-Instance Support)

/// <summary>
/// Manages multiple database connection instances.
/// Enables a single plugin to connect to multiple database servers simultaneously.
/// Thread-safe and supports connection pooling per instance.
/// </summary>
public sealed class ConnectionRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ConnectionInstance> _instances = new();
    private readonly SemaphoreSlim _registryLock = new(1, 1);
    private volatile bool _disposed;

    /// <summary>
    /// Gets all registered instance IDs.
    /// </summary>
    public IEnumerable<string> InstanceIds => _instances.Keys;

    /// <summary>
    /// Gets the count of registered instances.
    /// </summary>
    public int Count => _instances.Count;

    /// <summary>
    /// Registers a new connection instance.
    /// </summary>
    /// <param name="instanceId">Unique identifier for this instance.</param>
    /// <param name="config">Connection configuration.</param>
    /// <param name="connectionFactory">Factory to create connections.</param>
    /// <returns>The registered instance.</returns>
    public async Task<ConnectionInstance> RegisterAsync(
        string instanceId,
        ConnectionConfig config,
        Func<ConnectionConfig, Task<object>> connectionFactory)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("Instance ID cannot be empty.", nameof(instanceId));

        await _registryLock.WaitAsync();
        try
        {
            if (_instances.ContainsKey(instanceId))
                throw new InvalidOperationException($"Instance '{instanceId}' is already registered.");

            var instance = new ConnectionInstance(instanceId, config, connectionFactory);
            _instances[instanceId] = instance;

            return instance;
        }
        finally
        {
            _registryLock.Release();
        }
    }

    /// <summary>
    /// Gets an existing instance by ID.
    /// </summary>
    public ConnectionInstance? Get(string instanceId)
    {
        ThrowIfDisposed();
        return _instances.TryGetValue(instanceId, out var instance) ? instance : null;
    }

    /// <summary>
    /// Gets or creates a default instance.
    /// </summary>
    public async Task<ConnectionInstance> GetOrCreateDefaultAsync(
        ConnectionConfig config,
        Func<ConnectionConfig, Task<object>> connectionFactory)
    {
        const string defaultId = "__default__";
        var existing = Get(defaultId);
        if (existing != null) return existing;

        return await RegisterAsync(defaultId, config, connectionFactory);
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
    /// Gets all instances of a specific role.
    /// </summary>
    public IEnumerable<ConnectionInstance> GetByRole(ConnectionRole role)
    {
        return _instances.Values.Where(i => i.Config.Roles.HasFlag(role));
    }

    /// <summary>
    /// Gets the primary instance for a specific role.
    /// </summary>
    public ConnectionInstance? GetPrimaryForRole(ConnectionRole role)
    {
        return _instances.Values
            .Where(i => i.Config.Roles.HasFlag(role))
            .OrderByDescending(i => i.Config.Priority)
            .FirstOrDefault();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ConnectionRegistry));
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

/// <summary>
/// Represents a single database connection instance.
/// Manages connection state, pooling, and health.
/// </summary>
public sealed class ConnectionInstance : IAsyncDisposable
{
    private readonly Func<ConnectionConfig, Task<object>> _connectionFactory;
    private readonly ConcurrentQueue<object> _connectionPool = new();
    private readonly SemaphoreSlim _poolSemaphore;
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private int _activeConnections;
    private volatile bool _disposed;
    private object? _primaryConnection;

    /// <summary>
    /// Unique identifier for this instance.
    /// </summary>
    public string InstanceId { get; }

    /// <summary>
    /// Connection configuration.
    /// </summary>
    public ConnectionConfig Config { get; }

    /// <summary>
    /// Whether any connection is currently active.
    /// </summary>
    public bool IsConnected => _activeConnections > 0 || _primaryConnection != null;

    /// <summary>
    /// Number of active connections.
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
    /// Health status of this instance.
    /// </summary>
    public InstanceHealth Health { get; private set; } = InstanceHealth.Unknown;

    /// <summary>
    /// Connection statistics.
    /// </summary>
    public ConnectionStats Stats { get; } = new();

    public ConnectionInstance(
        string instanceId,
        ConnectionConfig config,
        Func<ConnectionConfig, Task<object>> connectionFactory)
    {
        InstanceId = instanceId;
        Config = config;
        _connectionFactory = connectionFactory;
        _poolSemaphore = new SemaphoreSlim(config.MaxPoolSize, config.MaxPoolSize);
    }

    /// <summary>
    /// Gets a connection from the pool or creates a new one.
    /// </summary>
    public async Task<PooledConnection> GetConnectionAsync(CancellationToken ct = default)
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
                return new PooledConnection(pooled, this);
            }

            // Create new connection
            await _createLock.WaitAsync(ct);
            try
            {
                var connection = await _connectionFactory(Config);
                Interlocked.Increment(ref _activeConnections);
                Stats.ConnectionsCreated++;
                LastActivity = DateTime.UtcNow;
                Health = InstanceHealth.Healthy;
                return new PooledConnection(connection, this);
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
            Health = InstanceHealth.Unhealthy;
            throw new DatabaseConnectionException($"Failed to get connection for instance '{InstanceId}'", ex);
        }
    }

    /// <summary>
    /// Gets or creates the primary (shared) connection.
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
            Health = InstanceHealth.Healthy;

            return _primaryConnection;
        }
        catch (Exception ex)
        {
            Stats.ConnectionErrors++;
            Health = InstanceHealth.Unhealthy;
            throw new DatabaseConnectionException($"Failed to create primary connection for instance '{InstanceId}'", ex);
        }
        finally
        {
            _createLock.Release();
        }
    }

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
        if (_connectionPool.Count < Config.MaxPoolSize)
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
            asyncDisposable.DisposeAsync().AsTask().Wait();
        }
        else if (connection is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ConnectionInstance));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Dispose primary connection
        if (_primaryConnection != null)
        {
            DisposeConnection(_primaryConnection);
            _primaryConnection = null;
        }

        // Dispose all pooled connections
        while (_connectionPool.TryDequeue(out var connection))
        {
            DisposeConnection(connection);
        }

        _poolSemaphore.Dispose();
        _createLock.Dispose();
    }
}

/// <summary>
/// Wraps a pooled connection for automatic return.
/// </summary>
public sealed class PooledConnection : IAsyncDisposable
{
    private readonly ConnectionInstance _instance;
    private bool _disposed;

    /// <summary>
    /// The underlying connection object.
    /// </summary>
    public object Connection { get; }

    internal PooledConnection(object connection, ConnectionInstance instance)
    {
        Connection = connection;
        _instance = instance;
    }

    /// <summary>
    /// Gets the connection as a specific type.
    /// </summary>
    public T GetConnection<T>() => (T)Connection;

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _instance.ReturnConnection(Connection);
        return ValueTask.CompletedTask;
    }
}

#endregion

#region Configuration

/// <summary>
/// Configuration for a database connection instance.
/// </summary>
public class ConnectionConfig
{
    /// <summary>
    /// Connection string for the database.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Display name for this connection.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Database engine type.
    /// </summary>
    public DatabaseEngine Engine { get; set; }

    /// <summary>
    /// Roles this connection serves (Storage, Index, Cache, Metadata).
    /// </summary>
    public ConnectionRole Roles { get; set; } = ConnectionRole.Storage;

    /// <summary>
    /// Priority for role selection (higher = preferred).
    /// </summary>
    public int Priority { get; set; } = 0;

    /// <summary>
    /// Default database/schema name.
    /// </summary>
    public string? DefaultDatabase { get; set; }

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Command/query timeout in seconds.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum connections in the pool.
    /// </summary>
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>
    /// Minimum connections to maintain.
    /// </summary>
    public int MinPoolSize { get; set; } = 5;

    /// <summary>
    /// Enable automatic retry on transient failures.
    /// </summary>
    public bool EnableRetry { get; set; } = true;

    /// <summary>
    /// Maximum retry attempts.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Enable SSL/TLS encryption.
    /// </summary>
    public bool UseSsl { get; set; } = false;

    /// <summary>
    /// Additional connection options.
    /// </summary>
    public Dictionary<string, string> Options { get; set; } = new();

    #region Factory Methods

    public static ConnectionConfig ForMySQL(string host, string database, string username, string password, int port = 3306)
        => new()
        {
            Engine = DatabaseEngine.MySQL,
            ConnectionString = $"Server={host};Port={port};Database={database};User={username};Password={password};",
            DefaultDatabase = database
        };

    public static ConnectionConfig ForPostgreSQL(string host, string database, string username, string password, int port = 5432)
        => new()
        {
            Engine = DatabaseEngine.PostgreSQL,
            ConnectionString = $"Host={host};Port={port};Database={database};Username={username};Password={password};",
            DefaultDatabase = database
        };

    public static ConnectionConfig ForSqlServer(string host, string database, string username, string password, int port = 1433)
        => new()
        {
            Engine = DatabaseEngine.SqlServer,
            ConnectionString = $"Server={host},{port};Database={database};User Id={username};Password={password};TrustServerCertificate=True;",
            DefaultDatabase = database
        };

    public static ConnectionConfig ForOracle(string host, string serviceName, string username, string password, int port = 1521)
        => new()
        {
            Engine = DatabaseEngine.Oracle,
            ConnectionString = $"Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={host})(PORT={port}))(CONNECT_DATA=(SERVICE_NAME={serviceName})));User Id={username};Password={password};",
            DefaultDatabase = serviceName
        };

    public static ConnectionConfig ForDb2(string host, string database, string username, string password, int port = 50000)
        => new()
        {
            Engine = DatabaseEngine.Db2,
            ConnectionString = $"Server={host}:{port};Database={database};UID={username};PWD={password};",
            DefaultDatabase = database
        };

    public static ConnectionConfig ForSQLite(string filePath, string? password = null)
        => new()
        {
            Engine = DatabaseEngine.SQLite,
            ConnectionString = password != null
                ? $"Data Source={filePath};Password={password};"
                : $"Data Source={filePath};",
            DefaultDatabase = Path.GetFileNameWithoutExtension(filePath)
        };

    public static ConnectionConfig ForSQLiteInMemory()
        => new()
        {
            Engine = DatabaseEngine.SQLite,
            ConnectionString = "Data Source=:memory:;",
            DefaultDatabase = "memory"
        };

    public static ConnectionConfig ForLiteDB(string filePath, string? password = null)
        => new()
        {
            Engine = DatabaseEngine.LiteDB,
            ConnectionString = password != null
                ? $"Filename={filePath};Password={password};"
                : $"Filename={filePath};",
            DefaultDatabase = Path.GetFileNameWithoutExtension(filePath)
        };

    public static ConnectionConfig ForDuckDB(string filePath)
        => new()
        {
            Engine = DatabaseEngine.DuckDB,
            ConnectionString = $"Data Source={filePath}",
            DefaultDatabase = Path.GetFileNameWithoutExtension(filePath)
        };

    public static ConnectionConfig ForRocksDB(string directory)
        => new()
        {
            Engine = DatabaseEngine.RocksDB,
            ConnectionString = directory,
            DefaultDatabase = Path.GetFileName(directory)
        };

    public static ConnectionConfig ForMongoDB(string connectionString, string? defaultDatabase = null)
        => new()
        {
            Engine = DatabaseEngine.MongoDB,
            ConnectionString = connectionString,
            DefaultDatabase = defaultDatabase
        };

    public static ConnectionConfig ForCouchDB(string host, string database, string? username = null, string? password = null, int port = 5984)
        => new()
        {
            Engine = DatabaseEngine.CouchDB,
            ConnectionString = username != null
                ? $"http://{username}:{password}@{host}:{port}"
                : $"http://{host}:{port}",
            DefaultDatabase = database
        };

    public static ConnectionConfig ForCassandra(string[] contactPoints, string keyspace, string? username = null, string? password = null)
        => new()
        {
            Engine = DatabaseEngine.Cassandra,
            ConnectionString = string.Join(",", contactPoints),
            DefaultDatabase = keyspace,
            Options = username != null
                ? new Dictionary<string, string> { ["username"] = username, ["password"] = password ?? "" }
                : new()
        };

    public static ConnectionConfig ForRedis(string host, int port = 6379, string? password = null, int database = 0)
        => new()
        {
            Engine = DatabaseEngine.Redis,
            ConnectionString = password != null
                ? $"{host}:{port},password={password},defaultDatabase={database}"
                : $"{host}:{port},defaultDatabase={database}",
            DefaultDatabase = database.ToString()
        };

    public static ConnectionConfig ForNeo4j(string host, string username, string password, int port = 7687)
        => new()
        {
            Engine = DatabaseEngine.Neo4j,
            ConnectionString = $"bolt://{host}:{port}",
            Options = new Dictionary<string, string> { ["username"] = username, ["password"] = password }
        };

    public static ConnectionConfig ForElasticsearch(string[] nodes, string? username = null, string? password = null)
        => new()
        {
            Engine = DatabaseEngine.Elasticsearch,
            ConnectionString = string.Join(",", nodes),
            Options = username != null
                ? new Dictionary<string, string> { ["username"] = username, ["password"] = password ?? "" }
                : new()
        };

    public static ConnectionConfig ForClickHouse(string host, string database, string username, string password, int port = 8123)
        => new()
        {
            Engine = DatabaseEngine.ClickHouse,
            ConnectionString = $"Host={host};Port={port};Database={database};User={username};Password={password};",
            DefaultDatabase = database
        };

    public static ConnectionConfig ForTimescaleDB(string host, string database, string username, string password, int port = 5432)
        => new()
        {
            Engine = DatabaseEngine.TimescaleDB,
            ConnectionString = $"Host={host};Port={port};Database={database};Username={username};Password={password};",
            DefaultDatabase = database
        };

    public static ConnectionConfig ForInfluxDB(string host, string bucket, string token, string org, int port = 8086)
        => new()
        {
            Engine = DatabaseEngine.InfluxDB,
            ConnectionString = $"http://{host}:{port}",
            DefaultDatabase = bucket,
            Options = new Dictionary<string, string> { ["token"] = token, ["org"] = org }
        };

    #endregion
}

/// <summary>
/// Roles a connection can serve.
/// </summary>
[Flags]
public enum ConnectionRole
{
    None = 0,
    Storage = 1,
    Index = 2,
    Cache = 4,
    Metadata = 8,
    All = Storage | Index | Cache | Metadata
}

/// <summary>
/// Supported database engines.
/// </summary>
public enum DatabaseEngine
{
    // In-Memory
    InMemory,

    // Relational
    MySQL,
    MariaDB,
    PostgreSQL,
    SqlServer,
    Oracle,
    Db2,
    SQLite,

    // Embedded
    LiteDB,
    DuckDB,
    RocksDB,
    BerkeleyDB,

    // NoSQL Document
    MongoDB,
    CouchDB,
    RavenDB,
    ArangoDB,

    // NoSQL Key-Value
    Redis,
    Memcached,
    DynamoDB,

    // NoSQL Column
    Cassandra,
    ScyllaDB,
    HBase,

    // Graph
    Neo4j,
    ArangoDB_Graph,
    JanusGraph,

    // Search
    Elasticsearch,
    OpenSearch,
    Solr,

    // Time Series
    InfluxDB,
    TimescaleDB,
    ClickHouse,
    QuestDB
}

/// <summary>
/// Health status of a connection instance.
/// </summary>
public enum InstanceHealth
{
    Unknown,
    Healthy,
    Degraded,
    Unhealthy
}

/// <summary>
/// Connection statistics.
/// </summary>
public class ConnectionStats
{
    public long ConnectionsCreated { get; set; }
    public long ConnectionErrors { get; set; }
    public long PoolHits { get; set; }
    public long PoolReturns { get; set; }
    public long QueriesExecuted { get; set; }
    public long QueryErrors { get; set; }
    public double TotalQueryTimeMs { get; set; }

    public double AverageQueryTimeMs => QueriesExecuted > 0 ? TotalQueryTimeMs / QueriesExecuted : 0;
}

#endregion

#region Function Adapters

/// <summary>
/// Base adapter for function-specific database operations.
/// </summary>
public abstract class DatabaseFunctionAdapter
{
    protected readonly ConnectionRegistry _registry;
    protected readonly string _defaultInstanceId;
    protected readonly JsonSerializerOptions _jsonOptions;

    protected DatabaseFunctionAdapter(ConnectionRegistry registry, string defaultInstanceId = "__default__")
    {
        _registry = registry;
        _defaultInstanceId = defaultInstanceId;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    protected ConnectionInstance GetInstance(string? instanceId = null)
    {
        var id = instanceId ?? _defaultInstanceId;
        return _registry.Get(id)
            ?? throw new InvalidOperationException($"Connection instance '{id}' not found.");
    }

    protected async Task<PooledConnection> GetConnectionAsync(string? instanceId = null, CancellationToken ct = default)
    {
        return await GetInstance(instanceId).GetConnectionAsync(ct);
    }
}

/// <summary>
/// Adapter for storage operations (save, load, delete, list).
/// </summary>
public class StorageFunctionAdapter : DatabaseFunctionAdapter
{
    public StorageFunctionAdapter(ConnectionRegistry registry, string defaultInstanceId = "__default__")
        : base(registry, defaultInstanceId) { }

    public virtual async Task SaveAsync(string key, Stream data, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        // Implementation depends on engine type
        throw new NotImplementedException("Override in engine-specific implementation");
    }

    public virtual async Task<Stream?> LoadAsync(string key, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }

    public virtual async Task DeleteAsync(string key, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }

    public virtual async Task<bool> ExistsAsync(string key, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }

    public virtual async IAsyncEnumerable<string> ListAsync(string prefix = "", string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
        yield break;
    }
}

/// <summary>
/// Adapter for indexing operations (add, remove, search, query).
/// </summary>
public class IndexFunctionAdapter : DatabaseFunctionAdapter
{
    public IndexFunctionAdapter(ConnectionRegistry registry, string defaultInstanceId = "__default__")
        : base(registry, defaultInstanceId) { }

    public virtual async Task IndexAsync(string id, Dictionary<string, object> document, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }

    public virtual async Task RemoveFromIndexAsync(string id, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }

    public virtual async Task<IEnumerable<SearchResult>> SearchAsync(string query, SearchOptions? options = null, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }

    public virtual async Task<IEnumerable<T>> QueryAsync<T>(string query, Dictionary<string, object>? parameters = null, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }
}

/// <summary>
/// Adapter for caching operations (get, set, remove, TTL).
/// </summary>
public class CacheFunctionAdapter : DatabaseFunctionAdapter
{
    public CacheFunctionAdapter(ConnectionRegistry registry, string defaultInstanceId = "__default__")
        : base(registry, defaultInstanceId) { }

    public virtual async Task<T?> GetAsync<T>(string key, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }

    public virtual async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }

    public virtual async Task<bool> RemoveAsync(string key, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }

    public virtual async Task<bool> ExistsAsync(string key, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }

    public virtual async Task<TimeSpan?> GetTtlAsync(string key, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }

    public virtual async Task SetTtlAsync(string key, TimeSpan ttl, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }

    public virtual async Task InvalidatePatternAsync(string pattern, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }
}

/// <summary>
/// Adapter for metadata operations (store, retrieve, query metadata).
/// </summary>
public class MetadataFunctionAdapter : DatabaseFunctionAdapter
{
    public MetadataFunctionAdapter(ConnectionRegistry registry, string defaultInstanceId = "__default__")
        : base(registry, defaultInstanceId) { }

    public virtual async Task StoreMetadataAsync(string entityId, Dictionary<string, object> metadata, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }

    public virtual async Task<Dictionary<string, object>?> GetMetadataAsync(string entityId, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }

    public virtual async Task UpdateMetadataAsync(string entityId, Dictionary<string, object> updates, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }

    public virtual async Task DeleteMetadataAsync(string entityId, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }

    public virtual async Task<IEnumerable<string>> QueryByMetadataAsync(Dictionary<string, object> criteria, string? instanceId = null, CancellationToken ct = default)
    {
        var instance = GetInstance(instanceId);
        throw new NotImplementedException("Override in engine-specific implementation");
    }
}

#endregion

#region Supporting Types

/// <summary>
/// Search result from index query.
/// </summary>
public class SearchResult
{
    public string Id { get; set; } = string.Empty;
    public double Score { get; set; }
    public Dictionary<string, object> Document { get; set; } = new();
    public Dictionary<string, string[]>? Highlights { get; set; }
}

/// <summary>
/// Options for search operations.
/// </summary>
public class SearchOptions
{
    public int Skip { get; set; } = 0;
    public int Take { get; set; } = 100;
    public string? SortBy { get; set; }
    public bool SortDescending { get; set; }
    public string[]? Fields { get; set; }
    public Dictionary<string, object>? Filters { get; set; }
    public bool IncludeHighlights { get; set; }
}

/// <summary>
/// Exception for database connection errors.
/// </summary>
public class DatabaseConnectionException : Exception
{
    public DatabaseConnectionException(string message) : base(message) { }
    public DatabaseConnectionException(string message, Exception inner) : base(message, inner) { }
}

#endregion
