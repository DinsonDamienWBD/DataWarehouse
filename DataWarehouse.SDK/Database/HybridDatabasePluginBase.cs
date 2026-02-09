using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Infrastructure;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.SDK.Database;

/// <summary>
/// Abstract base class for hybrid database storage plugins.
/// Extends IndexableStoragePluginBase to provide full Storage + Indexing + Caching support.
///
/// Features:
/// - Full IStorageProvider implementation with database backend
/// - Integrated ICacheableStorage with TTL support
/// - Integrated IMetadataIndex for search/indexing
/// - Multi-instance connection management via StorageConnectionRegistry
/// - Transaction support via message handlers
/// - Query capabilities via message handlers
/// - Schema management via message handlers
/// - Connection pooling management
///
/// URI format: {scheme}://{database}/{collection_or_table}/{document_id}
/// Example: mongodb://mydb/users/12345
///          sqlite://data.db/customers/cust_001
///          mysql://inventory/products/SKU123
/// </summary>
/// <typeparam name="TConfig">Configuration type for this database (e.g., RelationalDbConfig).</typeparam>
public abstract class HybridDatabasePluginBase<TConfig> : IndexableStoragePluginBase, IAsyncDisposable
    where TConfig : DatabaseConfigBase, new()
{
    protected readonly TConfig _config;
    protected readonly JsonSerializerOptions _jsonOptions;
    protected readonly SemaphoreSlim _connectionLock = new(1, 1);
    protected readonly StorageConnectionRegistry<TConfig> _connectionRegistry;
    protected bool _isConnected;

    /// <summary>
    /// The type of database (NoSQL, Embedded, Relational).
    /// </summary>
    public abstract DatabaseCategory DatabaseCategory { get; }

    /// <summary>
    /// The specific database engine name (e.g., "MySQL", "MongoDB", "SQLite").
    /// </summary>
    public abstract string Engine { get; }

    /// <summary>
    /// Whether the connection is currently active.
    /// </summary>
    public bool IsConnected => _isConnected;

    /// <summary>
    /// Access to the connection registry for multi-instance management.
    /// </summary>
    public StorageConnectionRegistry<TConfig> ConnectionRegistry => _connectionRegistry;

    /// <summary>
    /// Whether this database supports transactions.
    /// </summary>
    protected virtual bool SupportsTransactions => true;

    protected HybridDatabasePluginBase(TConfig? config = null)
    {
        _config = config ?? new TConfig();
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        _connectionRegistry = new StorageConnectionRegistry<TConfig>(CreateConnectionAsync);
    }

    /// <summary>
    /// Factory method to create a connection from configuration.
    /// Must be implemented by derived classes.
    /// </summary>
    protected abstract Task<object> CreateConnectionAsync(TConfig config);

    #region Metadata

    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["DatabaseCategory"] = DatabaseCategory.ToString();
        metadata["Engine"] = Engine;
        metadata["ConnectionString"] = _config.ConnectionString != null ? "***" : "not configured";
        metadata["IsConnected"] = _isConnected;
        metadata["SupportsTransactions"] = SupportsTransactions;
        metadata["SupportsQueries"] = true;
        metadata["SupportsConcurrency"] = true;
        metadata["SupportsListing"] = true;
        metadata["SupportsMultiInstance"] = true;
        metadata["RegisteredInstances"] = _connectionRegistry.Count;
        return metadata;
    }

    /// <summary>
    /// Standard database capabilities available via messages.
    /// </summary>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        var prefix = $"storage.{Scheme}";
        return
        [
            new() { Name = $"{prefix}.save", DisplayName = "Save Document", Description = "Save/upsert a document" },
            new() { Name = $"{prefix}.load", DisplayName = "Load Document", Description = "Load a document by ID" },
            new() { Name = $"{prefix}.delete", DisplayName = "Delete Document", Description = "Delete a document" },
            new() { Name = $"{prefix}.exists", DisplayName = "Exists", Description = "Check if document exists" },
            new() { Name = $"{prefix}.list", DisplayName = "List", Description = "List documents in collection" },
            new() { Name = $"{prefix}.query", DisplayName = "Query", Description = "Execute a query" },
            new() { Name = $"{prefix}.count", DisplayName = "Count", Description = "Count documents" },
            new() { Name = $"{prefix}.connect", DisplayName = "Connect", Description = "Open database connection" },
            new() { Name = $"{prefix}.disconnect", DisplayName = "Disconnect", Description = "Close database connection" },
            new() { Name = $"{prefix}.createdb", DisplayName = "Create Database", Description = "Create a new database" },
            new() { Name = $"{prefix}.dropdb", DisplayName = "Drop Database", Description = "Drop a database" },
            new() { Name = $"{prefix}.createcollection", DisplayName = "Create Collection", Description = "Create a collection/table" },
            new() { Name = $"{prefix}.dropcollection", DisplayName = "Drop Collection", Description = "Drop a collection/table" },
            new() { Name = $"{prefix}.listdatabases", DisplayName = "List Databases", Description = "List all databases" },
            new() { Name = $"{prefix}.listcollections", DisplayName = "List Collections", Description = "List collections in database" },
            // Multi-instance commands
            new() { Name = $"{prefix}.register", DisplayName = "Register Instance", Description = "Register a new database instance" },
            new() { Name = $"{prefix}.unregister", DisplayName = "Unregister Instance", Description = "Unregister a database instance" },
            new() { Name = $"{prefix}.instances", DisplayName = "List Instances", Description = "List all registered instances" },
            // Cache commands
            new() { Name = $"{prefix}.cache.stats", DisplayName = "Cache Stats", Description = "Get cache statistics" },
            new() { Name = $"{prefix}.cache.invalidate", DisplayName = "Invalidate Cache", Description = "Invalidate cache entries" },
            new() { Name = $"{prefix}.cache.cleanup", DisplayName = "Cleanup Cache", Description = "Remove expired entries" },
            // Index commands
            new() { Name = $"{prefix}.index.rebuild", DisplayName = "Rebuild Index", Description = "Rebuild the search index" },
            new() { Name = $"{prefix}.index.stats", DisplayName = "Index Stats", Description = "Get index statistics" },
            new() { Name = $"{prefix}.index.search", DisplayName = "Search Index", Description = "Search the index" }
        ];
    }

    #endregion

    #region Message Handlers

    /// <summary>
    /// Handles incoming messages for database operations.
    /// </summary>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        var prefix = $"storage.{Scheme}";
        var response = message.Type switch
        {
            var t when t == $"{prefix}.save" => await HandleSaveAsync(message),
            var t when t == $"{prefix}.load" => await HandleLoadAsync(message),
            var t when t == $"{prefix}.delete" => await HandleDeleteAsync(message),
            var t when t == $"{prefix}.exists" => await HandleExistsAsync(message),
            var t when t == $"{prefix}.query" => await HandleQueryAsync(message),
            var t when t == $"{prefix}.count" => await HandleCountAsync(message),
            var t when t == $"{prefix}.connect" => await HandleConnectAsync(message),
            var t when t == $"{prefix}.disconnect" => await HandleDisconnectAsync(message),
            var t when t == $"{prefix}.createdb" => await HandleCreateDatabaseAsync(message),
            var t when t == $"{prefix}.dropdb" => await HandleDropDatabaseAsync(message),
            var t when t == $"{prefix}.createcollection" => await HandleCreateCollectionAsync(message),
            var t when t == $"{prefix}.dropcollection" => await HandleDropCollectionAsync(message),
            var t when t == $"{prefix}.listdatabases" => await HandleListDatabasesAsync(message),
            var t when t == $"{prefix}.listcollections" => await HandleListCollectionsAsync(message),
            // Multi-instance
            var t when t == $"{prefix}.register" => await HandleRegisterInstanceAsync(message),
            var t when t == $"{prefix}.unregister" => await HandleUnregisterInstanceAsync(message),
            var t when t == $"{prefix}.instances" => HandleListInstancesAsync(message),
            // Cache
            var t when t == $"{prefix}.cache.stats" => await HandleCacheStatsAsync(message),
            var t when t == $"{prefix}.cache.invalidate" => await HandleCacheInvalidateAsync(message),
            var t when t == $"{prefix}.cache.cleanup" => await HandleCacheCleanupAsync(message),
            // Index
            var t when t == $"{prefix}.index.rebuild" => await HandleIndexRebuildAsync(message),
            var t when t == $"{prefix}.index.stats" => await HandleIndexStatsAsync(message),
            var t when t == $"{prefix}.index.search" => await HandleIndexSearchAsync(message),
            _ => null
        };
    }

    protected virtual async Task<MessageResponse> HandleSaveAsync(PluginMessage message)
    {
        var payload = message.Payload;

        var database = GetPayloadString(payload, "database");
        var collection = GetPayloadString(payload, "collection");
        var id = GetPayloadString(payload, "id");
        var data = payload.TryGetValue("data", out var dataObj) ? dataObj : null;
        var ttl = GetPayloadTimeSpan(payload, "ttl");

        if (string.IsNullOrEmpty(collection) || data == null)
            return MessageResponse.Error("Missing required parameters: collection, data");

        var uri = BuildUri(database, collection, id);
        var jsonData = JsonSerializer.Serialize(data, _jsonOptions);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonData));

        if (ttl.HasValue)
        {
            await SaveWithTtlAsync(uri, stream, ttl.Value);
        }
        else
        {
            await SaveAsync(uri, stream);
        }
        return MessageResponse.Ok(new { Database = database, Collection = collection, Id = id, Success = true });
    }

    protected virtual async Task<MessageResponse> HandleLoadAsync(PluginMessage message)
    {
        var payload = message.Payload;

        var database = GetPayloadString(payload, "database");
        var collection = GetPayloadString(payload, "collection");
        var id = GetPayloadString(payload, "id");

        if (string.IsNullOrEmpty(collection) || string.IsNullOrEmpty(id))
            return MessageResponse.Error("Missing required parameters: collection, id");

        var uri = BuildUri(database, collection, id);
        var stream = await LoadAsync(uri);
        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync();
        var data = JsonSerializer.Deserialize<object>(json, _jsonOptions);

        return MessageResponse.Ok(new { Database = database, Collection = collection, Id = id, Data = data });
    }

    protected virtual async Task<MessageResponse> HandleDeleteAsync(PluginMessage message)
    {
        var payload = message.Payload;

        var database = GetPayloadString(payload, "database");
        var collection = GetPayloadString(payload, "collection");
        var id = GetPayloadString(payload, "id");

        if (string.IsNullOrEmpty(collection) || string.IsNullOrEmpty(id))
            return MessageResponse.Error("Missing required parameters: collection, id");

        var uri = BuildUri(database, collection, id);
        await DeleteAsync(uri);
        return MessageResponse.Ok(new { Database = database, Collection = collection, Id = id, Deleted = true });
    }

    protected virtual async Task<MessageResponse> HandleExistsAsync(PluginMessage message)
    {
        var payload = message.Payload;

        var database = GetPayloadString(payload, "database");
        var collection = GetPayloadString(payload, "collection");
        var id = GetPayloadString(payload, "id");

        if (string.IsNullOrEmpty(collection) || string.IsNullOrEmpty(id))
            return MessageResponse.Error("Missing required parameters: collection, id");

        var uri = BuildUri(database, collection, id);
        var exists = await ExistsAsync(uri);
        return MessageResponse.Ok(new { Database = database, Collection = collection, Id = id, Exists = exists });
    }

    protected virtual async Task<MessageResponse> HandleQueryAsync(PluginMessage message)
    {
        var payload = message.Payload;

        var database = GetPayloadString(payload, "database");
        var collection = GetPayloadString(payload, "collection");
        var query = GetPayloadString(payload, "query");
        var instanceId = GetPayloadString(payload, "instanceId");
        var parameters = GetPayloadDictionary(payload, "parameters");

        var results = await ExecuteQueryAsync(database, collection, query, parameters, instanceId);
        return MessageResponse.Ok(new { Database = database, Collection = collection, Results = results });
    }

    protected virtual async Task<MessageResponse> HandleCountAsync(PluginMessage message)
    {
        var payload = message.Payload;

        var database = GetPayloadString(payload, "database");
        var collection = GetPayloadString(payload, "collection");
        var filter = GetPayloadString(payload, "filter");
        var instanceId = GetPayloadString(payload, "instanceId");

        var count = await CountAsync(database, collection, filter, instanceId);
        return MessageResponse.Ok(new { Database = database, Collection = collection, Count = count });
    }

    protected virtual async Task<MessageResponse> HandleConnectAsync(PluginMessage message)
    {
        var instanceId = GetPayloadString(message.Payload, "instanceId");
        await ConnectAsync(instanceId);
        return MessageResponse.Ok(new { Connected = _isConnected, InstanceId = instanceId ?? "default" });
    }

    protected virtual async Task<MessageResponse> HandleDisconnectAsync(PluginMessage message)
    {
        var instanceId = GetPayloadString(message.Payload, "instanceId");
        await DisconnectAsync(instanceId);
        return MessageResponse.Ok(new { Disconnected = true, InstanceId = instanceId ?? "default" });
    }

    protected virtual async Task<MessageResponse> HandleCreateDatabaseAsync(PluginMessage message)
    {
        var payload = message.Payload;

        var database = GetPayloadString(payload, "database");
        var instanceId = GetPayloadString(payload, "instanceId");
        if (string.IsNullOrEmpty(database))
            return MessageResponse.Error("Missing required parameter: database");

        await CreateDatabaseAsync(database, instanceId);
        return MessageResponse.Ok(new { Database = database, Created = true });
    }

    protected virtual async Task<MessageResponse> HandleDropDatabaseAsync(PluginMessage message)
    {
        var payload = message.Payload;

        var database = GetPayloadString(payload, "database");
        var instanceId = GetPayloadString(payload, "instanceId");
        if (string.IsNullOrEmpty(database))
            return MessageResponse.Error("Missing required parameter: database");

        await DropDatabaseAsync(database, instanceId);
        return MessageResponse.Ok(new { Database = database, Dropped = true });
    }

    protected virtual async Task<MessageResponse> HandleCreateCollectionAsync(PluginMessage message)
    {
        var payload = message.Payload;

        var database = GetPayloadString(payload, "database");
        var collection = GetPayloadString(payload, "collection");
        var instanceId = GetPayloadString(payload, "instanceId");
        var schema = GetPayloadDictionary(payload, "schema");

        if (string.IsNullOrEmpty(collection))
            return MessageResponse.Error("Missing required parameter: collection");

        await CreateCollectionAsync(database, collection, schema, instanceId);
        return MessageResponse.Ok(new { Database = database, Collection = collection, Created = true });
    }

    protected virtual async Task<MessageResponse> HandleDropCollectionAsync(PluginMessage message)
    {
        var payload = message.Payload;

        var database = GetPayloadString(payload, "database");
        var collection = GetPayloadString(payload, "collection");
        var instanceId = GetPayloadString(payload, "instanceId");

        if (string.IsNullOrEmpty(collection))
            return MessageResponse.Error("Missing required parameter: collection");

        await DropCollectionAsync(database, collection, instanceId);
        return MessageResponse.Ok(new { Database = database, Collection = collection, Dropped = true });
    }

    protected virtual async Task<MessageResponse> HandleListDatabasesAsync(PluginMessage message)
    {
        var instanceId = GetPayloadString(message.Payload, "instanceId");
        var databases = await ListDatabasesAsync(instanceId);
        return MessageResponse.Ok(new { Databases = databases });
    }

    protected virtual async Task<MessageResponse> HandleListCollectionsAsync(PluginMessage message)
    {
        var payload = message.Payload;

        var database = GetPayloadString(payload, "database");
        var instanceId = GetPayloadString(payload, "instanceId");
        var collections = await ListCollectionsAsync(database, instanceId);
        return MessageResponse.Ok(new { Database = database, Collections = collections });
    }

    // Multi-instance handlers
    protected virtual async Task<MessageResponse> HandleRegisterInstanceAsync(PluginMessage message)
    {
        var payload = message.Payload;
        var instanceId = GetPayloadString(payload, "instanceId");
        var connectionString = GetPayloadString(payload, "connectionString");
        var rolesStr = GetPayloadString(payload, "roles");

        if (string.IsNullOrEmpty(instanceId))
            return MessageResponse.Error("Missing required parameter: instanceId");

        var config = new TConfig();
        if (!string.IsNullOrEmpty(connectionString))
            config.ConnectionString = connectionString;

        var roles = ParseStorageRoles(rolesStr);
        await _connectionRegistry.RegisterAsync(instanceId, config, roles);

        return MessageResponse.Ok(new { InstanceId = instanceId, Registered = true, Roles = roles.ToString() });
    }

    protected virtual async Task<MessageResponse> HandleUnregisterInstanceAsync(PluginMessage message)
    {
        var instanceId = GetPayloadString(message.Payload, "instanceId");
        if (string.IsNullOrEmpty(instanceId))
            return MessageResponse.Error("Missing required parameter: instanceId");

        await _connectionRegistry.UnregisterAsync(instanceId);
        return MessageResponse.Ok(new { InstanceId = instanceId, Unregistered = true });
    }

    protected virtual MessageResponse HandleListInstancesAsync(PluginMessage message)
    {
        var instances = _connectionRegistry.GetAll().Select(i => new
        {
            i.InstanceId,
            i.Roles,
            i.Priority,
            i.IsConnected,
            i.LastActivity,
            i.Health
        });
        return MessageResponse.Ok(new { Instances = instances, Count = _connectionRegistry.Count });
    }

    // Cache handlers
    protected virtual async Task<MessageResponse> HandleCacheStatsAsync(PluginMessage message)
    {
        var stats = await GetCacheStatisticsAsync();
        return MessageResponse.Ok(stats);
    }

    protected virtual async Task<MessageResponse> HandleCacheInvalidateAsync(PluginMessage message)
    {
        var pattern = GetPayloadString(message.Payload, "pattern") ?? "*";
        var count = await InvalidatePatternAsync(pattern);
        return MessageResponse.Ok(new { Pattern = pattern, InvalidatedCount = count });
    }

    protected virtual async Task<MessageResponse> HandleCacheCleanupAsync(PluginMessage message)
    {
        var count = await CleanupExpiredAsync();
        return MessageResponse.Ok(new { CleanedUpCount = count });
    }

    // Index handlers
    protected virtual async Task<MessageResponse> HandleIndexRebuildAsync(PluginMessage message)
    {
        var count = await RebuildIndexAsync();
        return MessageResponse.Ok(new { IndexedCount = count, RebuildAt = DateTime.UtcNow });
    }

    protected virtual async Task<MessageResponse> HandleIndexStatsAsync(PluginMessage message)
    {
        var stats = await GetIndexStatisticsAsync();
        return MessageResponse.Ok(stats);
    }

    protected virtual async Task<MessageResponse> HandleIndexSearchAsync(PluginMessage message)
    {
        var query = GetPayloadString(message.Payload, "query") ?? "";
        var limitStr = GetPayloadString(message.Payload, "limit");
        var limit = int.TryParse(limitStr, out var l) ? l : 100;

        var results = await SearchIndexAsync(query, limit);
        return MessageResponse.Ok(new { Query = query, Results = results, Count = results.Length });
    }

    #endregion

    #region Abstract/Virtual Database Methods

    /// <summary>
    /// Connect to the database.
    /// </summary>
    protected virtual async Task ConnectAsync(string? instanceId = null)
    {
        if (string.IsNullOrEmpty(instanceId))
        {
            // Default instance
            if (_isConnected) return;

            await _connectionLock.WaitAsync();
            try
            {
                if (_isConnected) return;
                await _connectionRegistry.GetOrCreateDefaultAsync(_config);
                _isConnected = true;
            }
            finally
            {
                _connectionLock.Release();
            }
        }
        else
        {
            // Named instance
            var instance = _connectionRegistry.GetRequired(instanceId);
            await instance.GetPrimaryConnectionAsync();
        }
    }

    /// <summary>
    /// Disconnect from the database.
    /// </summary>
    protected virtual async Task DisconnectAsync(string? instanceId = null)
    {
        if (string.IsNullOrEmpty(instanceId))
        {
            await _connectionRegistry.ClearAsync();
            _isConnected = false;
        }
        else
        {
            await _connectionRegistry.UnregisterAsync(instanceId);
        }
    }

    /// <summary>
    /// Execute a query and return results.
    /// </summary>
    protected abstract Task<IEnumerable<object>> ExecuteQueryAsync(
        string? database, string? collection, string? query,
        Dictionary<string, object>? parameters, string? instanceId = null);

    /// <summary>
    /// Count documents matching filter.
    /// </summary>
    protected abstract Task<long> CountAsync(
        string? database, string? collection, string? filter, string? instanceId = null);

    /// <summary>
    /// Create a new database.
    /// </summary>
    protected abstract Task CreateDatabaseAsync(string database, string? instanceId = null);

    /// <summary>
    /// Drop a database.
    /// </summary>
    protected abstract Task DropDatabaseAsync(string database, string? instanceId = null);

    /// <summary>
    /// Create a collection/table.
    /// </summary>
    protected abstract Task CreateCollectionAsync(
        string? database, string collection, Dictionary<string, object>? schema, string? instanceId = null);

    /// <summary>
    /// Drop a collection/table.
    /// </summary>
    protected abstract Task DropCollectionAsync(string? database, string collection, string? instanceId = null);

    /// <summary>
    /// List all databases.
    /// </summary>
    protected abstract Task<IEnumerable<string>> ListDatabasesAsync(string? instanceId = null);

    /// <summary>
    /// List all collections in a database.
    /// </summary>
    protected abstract Task<IEnumerable<string>> ListCollectionsAsync(string? database, string? instanceId = null);

    #endregion

    #region Helper Methods

    protected Uri BuildUri(string? database, string? collection, string? id)
    {
        var path = string.IsNullOrEmpty(database)
            ? $"{collection ?? ""}/{id ?? ""}"
            : $"{database}/{collection ?? ""}/{id ?? ""}";

        return new Uri($"{Scheme}:///{path.Trim('/')}");
    }

    protected (string? database, string? collection, string? id) ParseUri(Uri uri)
    {
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.Length switch
        {
            0 => (null, null, null),
            1 => (segments[0], null, null),
            2 => (segments[0], segments[1], null),
            _ => (segments[0], segments[1], string.Join("/", segments.Skip(2)))
        };
    }

    /// <summary>
    /// Gets a string value from a payload dictionary.
    /// </summary>
    protected static string? GetPayloadString(Dictionary<string, object> dict, string key)
    {
        return dict.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    /// <summary>
    /// Gets a dictionary value from a payload dictionary.
    /// </summary>
    protected static Dictionary<string, object>? GetPayloadDictionary(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var value) && value is Dictionary<string, object> result)
            return result;
        return null;
    }

    /// <summary>
    /// Gets a TimeSpan value from a payload dictionary.
    /// </summary>
    protected static TimeSpan? GetPayloadTimeSpan(Dictionary<string, object> dict, string key)
    {
        var str = GetPayloadString(dict, key);
        if (string.IsNullOrEmpty(str)) return null;
        if (TimeSpan.TryParse(str, out var ts)) return ts;
        if (int.TryParse(str, out var seconds)) return TimeSpan.FromSeconds(seconds);
        return null;
    }

    protected async Task EnsureConnectedAsync(string? instanceId = null)
    {
        if (string.IsNullOrEmpty(instanceId))
        {
            if (_isConnected) return;

            await _connectionLock.WaitAsync();
            try
            {
                if (_isConnected) return;
                await ConnectAsync();
            }
            finally
            {
                _connectionLock.Release();
            }
        }
        else
        {
            var instance = _connectionRegistry.Get(instanceId);
            if (instance == null || !instance.IsConnected)
            {
                await ConnectAsync(instanceId);
            }
        }
    }

    protected StorageConnectionInstance<TConfig>? GetInstance(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
        {
            return _connectionRegistry.Get("__default__");
        }
        return _connectionRegistry.Get(instanceId);
    }

    private static StorageRole ParseStorageRoles(string? rolesStr)
    {
        if (string.IsNullOrEmpty(rolesStr)) return StorageRole.Primary;

        var roles = StorageRole.None;
        foreach (var part in rolesStr.Split(',', '|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<StorageRole>(part, true, out var role))
                roles |= role;
        }
        return roles == StorageRole.None ? StorageRole.Primary : roles;
    }

    #endregion

    #region IAsyncDisposable

    public async ValueTask DisposeAsync()
    {
        await _connectionRegistry.DisposeAsync();
        _connectionLock.Dispose();
        GC.SuppressFinalize(this);
    }

    #endregion
}

/// <summary>
/// Database category classification.
/// </summary>
public enum DatabaseCategory
{
    /// <summary>Document databases like MongoDB, CouchDB.</summary>
    NoSQL,
    /// <summary>Embedded databases like SQLite, LiteDB.</summary>
    Embedded,
    /// <summary>Relational databases like MySQL, PostgreSQL, SQL Server.</summary>
    Relational,
    /// <summary>Key-value stores like Redis, Memcached.</summary>
    KeyValue,
    /// <summary>Graph databases like Neo4j.</summary>
    Graph,
    /// <summary>Time-series databases like InfluxDB, TimescaleDB.</summary>
    TimeSeries,
    /// <summary>Search engines like Elasticsearch, Solr.</summary>
    Search,
    /// <summary>Vector databases like Pinecone, Milvus.</summary>
    Vector,
    /// <summary>Wide-column stores like Cassandra, HBase, ScyllaDB.</summary>
    WideColumn,
    /// <summary>Analytics databases like ClickHouse, Presto, Druid.</summary>
    Analytics,
    /// <summary>NewSQL databases like CockroachDB, TiDB, YugabyteDB, Vitess.</summary>
    NewSQL,
    /// <summary>Spatial databases like PostGIS.</summary>
    Spatial,
    /// <summary>Streaming platforms like Kafka, Pulsar.</summary>
    Streaming
}

/// <summary>
/// Base configuration for database plugins.
/// </summary>
public class DatabaseConfigBase
{
    /// <summary>Connection string for the database.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Default database name.</summary>
    public string? DefaultDatabase { get; set; }

    /// <summary>Connection timeout in seconds.</summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>Command timeout in seconds.</summary>
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>Maximum connection pool size.</summary>
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>Minimum connection pool size.</summary>
    public int MinPoolSize { get; set; } = 5;

    /// <summary>Enable automatic retry on transient failures.</summary>
    public bool EnableRetry { get; set; } = true;

    /// <summary>Maximum retry attempts.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Enable caching layer.</summary>
    public bool EnableCaching { get; set; } = true;

    /// <summary>Default cache TTL.</summary>
    public TimeSpan DefaultCacheTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Enable auto-indexing of saved documents.</summary>
    public bool EnableAutoIndexing { get; set; } = true;
}

/// <summary>
/// Result of a database query operation.
/// </summary>
public class DatabaseQueryResult
{
    /// <summary>Whether the query executed successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Number of rows affected by the query.</summary>
    public int RowsAffected { get; set; }

    /// <summary>Last inserted ID for insert operations.</summary>
    public long? LastInsertId { get; set; }

    /// <summary>Query execution time in milliseconds.</summary>
    public double ExecutionTimeMs { get; set; }

    /// <summary>Column names from the result set.</summary>
    public List<string> Columns { get; set; } = new();

    /// <summary>Row data as list of dictionaries.</summary>
    public List<Dictionary<string, object?>> Rows { get; set; } = new();

    /// <summary>Error message if the query failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Instance ID that executed the query.</summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// Creates a successful query result with rows.
    /// </summary>
    public static DatabaseQueryResult FromRows(
        List<Dictionary<string, object?>> rows,
        List<string>? columns = null,
        double executionTimeMs = 0,
        string? instanceId = null)
    {
        return new DatabaseQueryResult
        {
            Success = true,
            Rows = rows,
            Columns = columns ?? (rows.Count > 0 ? rows[0].Keys.ToList() : new List<string>()),
            RowsAffected = rows.Count,
            ExecutionTimeMs = executionTimeMs,
            InstanceId = instanceId
        };
    }

    /// <summary>
    /// Creates a successful non-query result.
    /// </summary>
    public static DatabaseQueryResult FromAffected(
        int rowsAffected,
        long? lastInsertId = null,
        double executionTimeMs = 0,
        string? instanceId = null)
    {
        return new DatabaseQueryResult
        {
            Success = true,
            RowsAffected = rowsAffected,
            LastInsertId = lastInsertId,
            ExecutionTimeMs = executionTimeMs,
            InstanceId = instanceId
        };
    }

    /// <summary>
    /// Creates an error result.
    /// </summary>
    public static DatabaseQueryResult Error(string message, string? instanceId = null)
    {
        return new DatabaseQueryResult
        {
            Success = false,
            ErrorMessage = message,
            InstanceId = instanceId
        };
    }
}
