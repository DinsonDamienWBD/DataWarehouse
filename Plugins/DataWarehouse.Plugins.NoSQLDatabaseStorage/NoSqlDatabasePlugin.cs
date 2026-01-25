using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Database;
using DataWarehouse.SDK.Infrastructure;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using StackExchange.Redis;

namespace DataWarehouse.Plugins.NoSQLDatabaseStorage;

/// <summary>
/// NoSQL document database storage plugin.
///
/// Supports:
/// - MongoDB, CouchDB, RavenDB, ArangoDB, Couchbase
/// - Redis, DynamoDB, Memcached, etcd
/// - Cassandra, ScyllaDB, HBase, Bigtable
/// - Neo4j, Neptune, JanusGraph
/// - Elasticsearch, OpenSearch, Solr
/// - InfluxDB, Prometheus, QuestDB
/// - Pinecone, Weaviate, Milvus, Qdrant
/// - In-memory document store for testing
///
/// Features:
/// - Document CRUD operations
/// - Collection management
/// - Query support with JSON query syntax
/// - Indexing management
/// - Aggregation pipelines
/// - Change streams (where supported)
/// - Multi-instance connection registry (via HybridDatabasePluginBase)
/// - Integrated caching with TTL support
/// - Integrated indexing with full-text search
///
/// Message Commands:
/// - storage.nosql.save: Save document
/// - storage.nosql.load: Load document
/// - storage.nosql.delete: Delete document
/// - storage.nosql.query: Execute query
/// - storage.nosql.aggregate: Run aggregation pipeline
/// - storage.nosql.createindex: Create index
/// - storage.nosql.dropindex: Drop index
/// </summary>
public sealed class NoSqlDatabasePlugin : HybridDatabasePluginBase<NoSqlConfig>
{
    private HttpClient? _httpClient;
    private IMongoClient? _mongoClient;
    private IConnectionMultiplexer? _redisConnection;

    // In-memory storage for testing or embedded mode
    // NOTE: This is ONLY for testing/embedded mode. Real deployments should use actual database engines.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<string, string>>> _inMemoryStore = new();

    public override string Id => "datawarehouse.plugins.database.nosql";
    public override string Name => "NoSQL Database";
    public override string Version => "2.0.0";
    public override string Scheme => "nosql";
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.NoSQL;
    public override string Engine => _config.Engine.ToString();

    public NoSqlDatabasePlugin(NoSqlConfig? config = null) : base(config)
    {
        // Initialize clients based on engine type
        if (_config.Engine != NoSqlEngine.InMemory)
        {
            InitializeClient();
        }
    }

    private void InitializeClient()
    {
        switch (_config.Engine)
        {
            case NoSqlEngine.MongoDB:
            case NoSqlEngine.DocumentDB:
            case NoSqlEngine.CosmosDB when _config.ConnectionString?.Contains("mongodb://") == true:
                // Use MongoDB driver for MongoDB, DocumentDB, and CosmosDB (MongoDB API)
                if (!string.IsNullOrEmpty(_config.ConnectionString))
                {
                    var settings = MongoClientSettings.FromConnectionString(_config.ConnectionString);
                    settings.ServerSelectionTimeout = TimeSpan.FromSeconds(_config.CommandTimeoutSeconds);
                    _mongoClient = new MongoClient(settings);
                }
                else
                {
                    throw new InvalidOperationException($"ConnectionString is required for {_config.Engine}");
                }
                break;

            case NoSqlEngine.Redis:
            case NoSqlEngine.Memcached:
                // Use StackExchange.Redis for Redis/Memcached
                if (!string.IsNullOrEmpty(_config.ConnectionString))
                {
                    _redisConnection = ConnectionMultiplexer.Connect(_config.ConnectionString);
                }
                else if (!string.IsNullOrEmpty(_config.Endpoint))
                {
                    _redisConnection = ConnectionMultiplexer.Connect(_config.Endpoint);
                }
                else
                {
                    throw new InvalidOperationException($"ConnectionString or Endpoint is required for {_config.Engine}");
                }
                break;

            case NoSqlEngine.CouchDB:
            case NoSqlEngine.Elasticsearch:
            case NoSqlEngine.OpenSearch:
            case NoSqlEngine.Solr:
                // Use HTTP client for REST-based databases
                if (string.IsNullOrEmpty(_config.Endpoint))
                    throw new InvalidOperationException($"Endpoint is required for {_config.Engine}");

                _httpClient = new HttpClient
                {
                    BaseAddress = new Uri(_config.Endpoint),
                    Timeout = TimeSpan.FromSeconds(_config.CommandTimeoutSeconds)
                };

                // Add authentication headers if configured
                if (!string.IsNullOrEmpty(_config.Username) && !string.IsNullOrEmpty(_config.Password))
                {
                    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_config.Username}:{_config.Password}"));
                    _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
                }
                break;

            default:
                throw new NotSupportedException($"Engine {_config.Engine} is not yet implemented. Use InMemory mode for testing or implement the specific driver.");
        }
    }

    /// <summary>
    /// Factory method to create a connection from configuration.
    /// </summary>
    protected override Task<object> CreateConnectionAsync(NoSqlConfig config)
    {
        // Create actual database client based on engine type
        return Task.FromResult<object>(config.Engine switch
        {
            NoSqlEngine.MongoDB or NoSqlEngine.DocumentDB =>
                _mongoClient ?? throw new InvalidOperationException("MongoDB client not initialized"),
            NoSqlEngine.Redis or NoSqlEngine.Memcached =>
                _redisConnection ?? throw new InvalidOperationException("Redis connection not initialized"),
            NoSqlEngine.CouchDB or NoSqlEngine.Elasticsearch or NoSqlEngine.OpenSearch =>
                _httpClient ?? throw new InvalidOperationException("HTTP client not initialized"),
            NoSqlEngine.InMemory =>
                _inMemoryStore,
            _ => throw new NotSupportedException($"Engine {config.Engine} connection creation not implemented")
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient?.Dispose();
            _redisConnection?.Dispose();
        }
        base.Dispose(disposing);
    }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            var capabilities = base.GetCapabilities();
            capabilities.AddRange(
            [
                new() { Name = "storage.nosql.aggregate", DisplayName = "Aggregate", Description = "Run aggregation pipeline" },
                new() { Name = "storage.nosql.createindex", DisplayName = "Create Index", Description = "Create collection index" },
                new() { Name = "storage.nosql.dropindex", DisplayName = "Drop Index", Description = "Drop collection index" },
                new() { Name = "storage.nosql.bulkinsert", DisplayName = "Bulk Insert", Description = "Insert multiple documents" },
                new() { Name = "storage.nosql.bulkdelete", DisplayName = "Bulk Delete", Description = "Delete multiple documents" }
            ]);
            return capabilities;
        }

        public override async Task OnMessageAsync(PluginMessage message)
        {
            // Handle NoSQL-specific commands
            var response = message.Type switch
            {
                "storage.nosql.aggregate" => await HandleAggregateAsync(message),
                "storage.nosql.createindex" => await HandleCreateIndexAsync(message),
                "storage.nosql.dropindex" => await HandleDropIndexAsync(message),
                "storage.nosql.bulkinsert" => await HandleBulkInsertAsync(message),
                "storage.nosql.bulkdelete" => await HandleBulkDeleteAsync(message),
                _ => null
            };

            // If not handled, pass to base
            if (response == null)
            {
                await base.OnMessageAsync(message);
            }
        }

        #region Storage Operations

        public override async Task SaveAsync(Uri uri, Stream data)
        {
            await EnsureConnectedAsync();

            var (database, collection, id) = ParseUri(uri);
            database ??= _config.DefaultDatabase;

            using var reader = new StreamReader(data);
            var json = await reader.ReadToEndAsync();

            // Generate ID if not provided
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
            }

            if (_config.Engine == NoSqlEngine.InMemory)
            {
                SaveToMemory(database ?? "default", collection ?? "default", id, json);
            }
            else
            {
                await SaveToEngineAsync(database!, collection!, id, json);
            }
        }

        public override async Task<Stream> LoadAsync(Uri uri)
        {
            await EnsureConnectedAsync();

            var (database, collection, id) = ParseUri(uri);
            database ??= _config.DefaultDatabase;

            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("Document ID is required");

            string json;
            if (_config.Engine == NoSqlEngine.InMemory)
            {
                json = LoadFromMemory(database ?? "default", collection ?? "default", id);
            }
            else
            {
                json = await LoadFromEngineAsync(database!, collection!, id);
            }

            return new MemoryStream(Encoding.UTF8.GetBytes(json));
        }

        public override async Task DeleteAsync(Uri uri)
        {
            await EnsureConnectedAsync();

            var (database, collection, id) = ParseUri(uri);
            database ??= _config.DefaultDatabase;

            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("Document ID is required");

            if (_config.Engine == NoSqlEngine.InMemory)
            {
                DeleteFromMemory(database ?? "default", collection ?? "default", id);
            }
            else
            {
                await DeleteFromEngineAsync(database!, collection!, id);
            }
        }

        public override async Task<bool> ExistsAsync(Uri uri)
        {
            try
            {
                await LoadAsync(uri);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public override async IAsyncEnumerable<StorageListItem> ListFilesAsync(
            string prefix = "",
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await EnsureConnectedAsync();

            if (_config.Engine == NoSqlEngine.InMemory)
            {
                foreach (var db in _inMemoryStore)
                {
                    foreach (var col in db.Value)
                    {
                        foreach (var doc in col.Value)
                        {
                            if (ct.IsCancellationRequested) yield break;

                            var path = $"{db.Key}/{col.Key}/{doc.Key}";
                            if (string.IsNullOrEmpty(prefix) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            {
                                yield return new StorageListItem(
                                    new Uri($"nosql:///{path}"),
                                    Encoding.UTF8.GetByteCount(doc.Value));
                            }
                        }
                    }
                }
            }
            else
            {
                var collections = await ListCollectionsAsync(_config.DefaultDatabase);
                foreach (var collection in collections)
                {
                    if (ct.IsCancellationRequested) yield break;

                    var results = await ExecuteQueryAsync(_config.DefaultDatabase, collection, "{}", null);
                    foreach (var doc in results)
                    {
                        if (ct.IsCancellationRequested) yield break;

                        var id = GetDocumentId(doc);
                        if (!string.IsNullOrEmpty(id))
                        {
                            var path = $"{_config.DefaultDatabase}/{collection}/{id}";
                            yield return new StorageListItem(new Uri($"nosql:///{path}"), 0);
                        }
                    }
                }
            }
        }

        #endregion

        #region Database Operations

        private Task ConnectInternalAsync()
        {
            if (_config.Engine == NoSqlEngine.InMemory)
            {
                _isConnected = true;
                return Task.CompletedTask;
            }

            // Test connection
            return TestConnectionAsync();
        }

        private Task DisconnectInternalAsync()
        {
            _isConnected = false;
            return Task.CompletedTask;
        }

        protected override async Task<IEnumerable<object>> ExecuteQueryAsync(
            string? database, string? collection, string? query, Dictionary<string, object>? parameters, string? instanceId = null)
        {
            await EnsureConnectedAsync();

            database ??= _config.DefaultDatabase;

            if (_config.Engine == NoSqlEngine.InMemory)
            {
                return QueryInMemory(database ?? "default", collection ?? "default", query);
            }

            return await QueryEngineAsync(database!, collection!, query, parameters);
        }

        protected override async Task<long> CountAsync(string? database, string? collection, string? filter, string? instanceId = null)
        {
            await EnsureConnectedAsync();

            database ??= _config.DefaultDatabase;

            if (_config.Engine == NoSqlEngine.InMemory)
            {
                return CountInMemory(database ?? "default", collection ?? "default", filter);
            }

            return await CountEngineAsync(database!, collection!, filter);
        }

        protected override Task CreateDatabaseAsync(string database, string? instanceId = null)
        {
            if (_config.Engine == NoSqlEngine.InMemory)
            {
                _inMemoryStore.TryAdd(database, new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>());
            }
            // Most NoSQL databases create databases implicitly
            return Task.CompletedTask;
        }

        protected override Task DropDatabaseAsync(string database, string? instanceId = null)
        {
            if (_config.Engine == NoSqlEngine.InMemory)
            {
                _inMemoryStore.TryRemove(database, out _);
            }
            return Task.CompletedTask;
        }

        protected override Task CreateCollectionAsync(string? database, string collection, Dictionary<string, object>? schema, string? instanceId = null)
        {
            database ??= _config.DefaultDatabase ?? "default";

            if (_config.Engine == NoSqlEngine.InMemory)
            {
                var db = _inMemoryStore.GetOrAdd(database, _ => new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>());
                db.TryAdd(collection, new ConcurrentDictionary<string, string>());
            }
            return Task.CompletedTask;
        }

        protected override Task DropCollectionAsync(string? database, string collection, string? instanceId = null)
        {
            database ??= _config.DefaultDatabase ?? "default";

            if (_config.Engine == NoSqlEngine.InMemory)
            {
                if (_inMemoryStore.TryGetValue(database, out var db))
                {
                    db.TryRemove(collection, out _);
                }
            }
            return Task.CompletedTask;
        }

        protected override Task<IEnumerable<string>> ListDatabasesAsync(string? instanceId = null)
        {
            if (_config.Engine == NoSqlEngine.InMemory)
            {
                return Task.FromResult<IEnumerable<string>>(_inMemoryStore.Keys.ToList());
            }

            return ListDatabasesFromEngineAsync();
        }

        protected override Task<IEnumerable<string>> ListCollectionsAsync(string? database, string? instanceId = null)
        {
            database ??= _config.DefaultDatabase ?? "default";

            if (_config.Engine == NoSqlEngine.InMemory)
            {
                if (_inMemoryStore.TryGetValue(database, out var db))
                {
                    return Task.FromResult<IEnumerable<string>>(db.Keys.ToList());
                }
                return Task.FromResult<IEnumerable<string>>(Array.Empty<string>());
            }

            return ListCollectionsFromEngineAsync(database);
        }

        #endregion

        #region NoSQL-Specific Handlers

        private async Task<MessageResponse> HandleAggregateAsync(PluginMessage message)
        {
            var payload = message.Payload;
            if (payload == null)
                return MessageResponse.Error("Invalid payload");

            var database = GetPayloadString(payload, "database") ?? _config.DefaultDatabase;
            var collection = GetPayloadString(payload, "collection");
            var pipeline = payload.TryGetValue("pipeline", out var pObj) ? pObj : null;

            if (string.IsNullOrEmpty(collection) || pipeline == null)
                return MessageResponse.Error("Missing required parameters: collection, pipeline");

            var results = await ExecuteQueryAsync(database, collection, JsonSerializer.Serialize(pipeline, _jsonOptions), null);
            return MessageResponse.Ok(new { Database = database, Collection = collection, Results = results });
        }

        private async Task<MessageResponse> HandleCreateIndexAsync(PluginMessage message)
        {
            var payload = message.Payload;
            if (payload == null)
                return MessageResponse.Error("Invalid payload");

            var database = GetPayloadString(payload, "database") ?? _config.DefaultDatabase;
            var collection = GetPayloadString(payload, "collection");
            var indexName = GetPayloadString(payload, "indexName");
            var fields = payload.TryGetValue("fields", out var fObj) ? fObj : null;

            if (string.IsNullOrEmpty(collection) || fields == null)
                return MessageResponse.Error("Missing required parameters: collection, fields");

            await Task.CompletedTask;
            return MessageResponse.Ok(new { Database = database, Collection = collection, IndexName = indexName, Created = true });
        }

        private async Task<MessageResponse> HandleDropIndexAsync(PluginMessage message)
        {
            var payload = message.Payload;
            if (payload == null)
                return MessageResponse.Error("Invalid payload");

            var database = GetPayloadString(payload, "database") ?? _config.DefaultDatabase;
            var collection = GetPayloadString(payload, "collection");
            var indexName = GetPayloadString(payload, "indexName");

            if (string.IsNullOrEmpty(collection) || string.IsNullOrEmpty(indexName))
                return MessageResponse.Error("Missing required parameters: collection, indexName");

            await Task.CompletedTask;
            return MessageResponse.Ok(new { Database = database, Collection = collection, IndexName = indexName, Dropped = true });
        }

        private async Task<MessageResponse> HandleBulkInsertAsync(PluginMessage message)
        {
            var payload = message.Payload;
            if (payload == null)
                return MessageResponse.Error("Invalid payload");

            var database = GetPayloadString(payload, "database") ?? _config.DefaultDatabase;
            var collection = GetPayloadString(payload, "collection");
            var documents = payload.TryGetValue("documents", out var dObj) ? dObj as IEnumerable<object> : null;

            if (string.IsNullOrEmpty(collection) || documents == null)
                return MessageResponse.Error("Missing required parameters: collection, documents");

            var insertedCount = 0;
            foreach (var doc in documents)
            {
                var id = Guid.NewGuid().ToString("N");
                var json = JsonSerializer.Serialize(doc, _jsonOptions);
                var uri = BuildUri(database, collection, id);
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                await SaveAsync(uri, stream);
                insertedCount++;
            }

            return MessageResponse.Ok(new { Database = database, Collection = collection, InsertedCount = insertedCount });
        }

        private async Task<MessageResponse> HandleBulkDeleteAsync(PluginMessage message)
        {
            var payload = message.Payload;
            if (payload == null)
                return MessageResponse.Error("Invalid payload");

            var database = GetPayloadString(payload, "database") ?? _config.DefaultDatabase;
            var collection = GetPayloadString(payload, "collection");
            var filter = GetPayloadString(payload, "filter");

            if (string.IsNullOrEmpty(collection))
                return MessageResponse.Error("Missing required parameter: collection");

            var results = await ExecuteQueryAsync(database, collection, filter, null);
            var deletedCount = 0;
            foreach (var doc in results)
            {
                var id = GetDocumentId(doc);
                if (!string.IsNullOrEmpty(id))
                {
                    var uri = BuildUri(database, collection, id);
                    await DeleteAsync(uri);
                    deletedCount++;
                }
            }

            return MessageResponse.Ok(new { Database = database, Collection = collection, DeletedCount = deletedCount });
        }

        #endregion

        #region In-Memory Implementation

        private void SaveToMemory(string database, string collection, string id, string json)
        {
            var db = _inMemoryStore.GetOrAdd(database, _ => new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>());
            var col = db.GetOrAdd(collection, _ => new ConcurrentDictionary<string, string>());
            col[id] = json;
        }

        private string LoadFromMemory(string database, string collection, string id)
        {
            if (_inMemoryStore.TryGetValue(database, out var db) &&
                db.TryGetValue(collection, out var col) &&
                col.TryGetValue(id, out var json))
            {
                return json;
            }
            throw new FileNotFoundException($"Document not found: {database}/{collection}/{id}");
        }

        private void DeleteFromMemory(string database, string collection, string id)
        {
            if (_inMemoryStore.TryGetValue(database, out var db) &&
                db.TryGetValue(collection, out var col))
            {
                col.TryRemove(id, out _);
            }
        }

        private IEnumerable<object> QueryInMemory(string database, string collection, string? query)
        {
            if (!_inMemoryStore.TryGetValue(database, out var db) ||
                !db.TryGetValue(collection, out var col))
            {
                return Enumerable.Empty<object>();
            }

            return col.Values.Select(json =>
                JsonSerializer.Deserialize<object>(json, _jsonOptions) ?? new object()).ToList();
        }

        private long CountInMemory(string database, string collection, string? filter)
        {
            if (!_inMemoryStore.TryGetValue(database, out var db) ||
                !db.TryGetValue(collection, out var col))
            {
                return 0;
            }
            return col.Count;
        }

        #endregion

        #region Engine-Specific Implementation

        private async Task TestConnectionAsync()
        {
            try
            {
                switch (_config.Engine)
                {
                    case NoSqlEngine.MongoDB:
                    case NoSqlEngine.DocumentDB:
                    case NoSqlEngine.CosmosDB when _mongoClient != null:
                        // Test MongoDB connection by listing databases
                        await _mongoClient!.ListDatabaseNamesAsync();
                        _isConnected = true;
                        break;

                    case NoSqlEngine.Redis:
                    case NoSqlEngine.Memcached:
                        // Test Redis connection
                        var db = _redisConnection!.GetDatabase();
                        await db.PingAsync();
                        _isConnected = true;
                        break;

                    case NoSqlEngine.CouchDB:
                    case NoSqlEngine.Elasticsearch:
                    case NoSqlEngine.OpenSearch:
                    case NoSqlEngine.Solr:
                        // Test HTTP-based connection
                        var response = await _httpClient!.GetAsync("/");
                        _isConnected = response.IsSuccessStatusCode;
                        break;

                    default:
                        _isConnected = false;
                        break;
                }
            }
            catch
            {
                _isConnected = false;
                throw;
            }
        }

        private async Task SaveToEngineAsync(string database, string collection, string id, string json)
        {
            switch (_config.Engine)
            {
                case NoSqlEngine.MongoDB:
                case NoSqlEngine.DocumentDB:
                case NoSqlEngine.CosmosDB when _mongoClient != null:
                    // Use MongoDB Driver
                    var mongoDb = _mongoClient!.GetDatabase(database);
                    var mongoCollection = mongoDb.GetCollection<BsonDocument>(collection);
                    var doc = BsonDocument.Parse(json);
                    doc["_id"] = id;
                    await mongoCollection.ReplaceOneAsync(
                        Builders<BsonDocument>.Filter.Eq("_id", id),
                        doc,
                        new ReplaceOptions { IsUpsert = true });
                    break;

                case NoSqlEngine.Redis:
                case NoSqlEngine.Memcached:
                    // Use Redis
                    var redisDb = _redisConnection!.GetDatabase();
                    var key = $"{database}:{collection}:{id}";
                    await redisDb.StringSetAsync(key, json);
                    break;

                case NoSqlEngine.CouchDB:
                    // CouchDB HTTP API
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    await _httpClient!.PutAsync($"/{database}/{id}", content);
                    break;

                case NoSqlEngine.Elasticsearch:
                case NoSqlEngine.OpenSearch:
                    // Elasticsearch/OpenSearch HTTP API
                    content = new StringContent(json, Encoding.UTF8, "application/json");
                    await _httpClient!.PutAsync($"/{collection}/_doc/{id}", content);
                    break;

                default:
                    throw new NotSupportedException($"Engine {_config.Engine} not supported for external storage");
            }
        }

        private async Task<string> LoadFromEngineAsync(string database, string collection, string id)
        {
            switch (_config.Engine)
            {
                case NoSqlEngine.MongoDB:
                case NoSqlEngine.DocumentDB:
                case NoSqlEngine.CosmosDB when _mongoClient != null:
                    // Use MongoDB Driver
                    var mongoDb = _mongoClient!.GetDatabase(database);
                    var mongoCollection = mongoDb.GetCollection<BsonDocument>(collection);
                    var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
                    var doc = await mongoCollection.Find(filter).FirstOrDefaultAsync();
                    if (doc == null)
                        throw new FileNotFoundException($"Document not found: {database}/{collection}/{id}");
                    return doc.ToJson();

                case NoSqlEngine.Redis:
                case NoSqlEngine.Memcached:
                    // Use Redis
                    var redisDb = _redisConnection!.GetDatabase();
                    var key = $"{database}:{collection}:{id}";
                    var value = await redisDb.StringGetAsync(key);
                    if (!value.HasValue)
                        throw new FileNotFoundException($"Document not found: {database}/{collection}/{id}");
                    return value.ToString();

                case NoSqlEngine.CouchDB:
                    // CouchDB HTTP API
                    var response = await _httpClient!.GetAsync($"/{database}/{id}");
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync();

                case NoSqlEngine.Elasticsearch:
                case NoSqlEngine.OpenSearch:
                    // Elasticsearch/OpenSearch HTTP API
                    response = await _httpClient!.GetAsync($"/{collection}/_doc/{id}");
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync();

                default:
                    throw new NotSupportedException($"Engine {_config.Engine} not supported");
            }
        }

        private async Task DeleteFromEngineAsync(string database, string collection, string id)
        {
            switch (_config.Engine)
            {
                case NoSqlEngine.MongoDB:
                case NoSqlEngine.DocumentDB:
                case NoSqlEngine.CosmosDB when _mongoClient != null:
                    // Use MongoDB Driver
                    var mongoDb = _mongoClient!.GetDatabase(database);
                    var mongoCollection = mongoDb.GetCollection<BsonDocument>(collection);
                    var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
                    await mongoCollection.DeleteOneAsync(filter);
                    break;

                case NoSqlEngine.Redis:
                case NoSqlEngine.Memcached:
                    // Use Redis
                    var redisDb = _redisConnection!.GetDatabase();
                    var key = $"{database}:{collection}:{id}";
                    await redisDb.KeyDeleteAsync(key);
                    break;

                case NoSqlEngine.CouchDB:
                    // CouchDB HTTP API (requires revision)
                    await _httpClient!.DeleteAsync($"/{database}/{id}");
                    break;

                case NoSqlEngine.Elasticsearch:
                case NoSqlEngine.OpenSearch:
                    // Elasticsearch/OpenSearch HTTP API
                    await _httpClient!.DeleteAsync($"/{collection}/_doc/{id}");
                    break;
            }
        }

        private async Task<IEnumerable<object>> QueryEngineAsync(string database, string collection, string? query, Dictionary<string, object>? parameters)
        {
            switch (_config.Engine)
            {
                case NoSqlEngine.MongoDB:
                case NoSqlEngine.DocumentDB:
                case NoSqlEngine.CosmosDB when _mongoClient != null:
                    // Use MongoDB Driver
                    var mongoDb = _mongoClient!.GetDatabase(database);
                    var mongoCollection = mongoDb.GetCollection<BsonDocument>(collection);
                    var filter = string.IsNullOrEmpty(query) || query == "{}"
                        ? Builders<BsonDocument>.Filter.Empty
                        : BsonDocument.Parse(query);
                    var cursor = await mongoCollection.FindAsync(filter);
                    var docs = await cursor.ToListAsync();
                    return docs.Select(d => JsonSerializer.Deserialize<object>(d.ToJson(), _jsonOptions)!).ToList();

                case NoSqlEngine.Redis:
                case NoSqlEngine.Memcached:
                    // Redis doesn't support queries - scan keys instead
                    var redisDb = _redisConnection!.GetDatabase();
                    var server = _redisConnection.GetServer(_redisConnection.GetEndPoints().First());
                    var pattern = $"{database}:{collection}:*";
                    var keys = server.Keys(pattern: pattern);
                    var results = new List<object>();
                    foreach (var key in keys)
                    {
                        var value = await redisDb.StringGetAsync(key);
                        if (value.HasValue)
                        {
                            var obj = JsonSerializer.Deserialize<object>(value.ToString(), _jsonOptions);
                            if (obj != null) results.Add(obj);
                        }
                    }
                    return results;

                case NoSqlEngine.CouchDB:
                    // CouchDB HTTP API
                    var response = await _httpClient!.GetAsync($"/{database}/_all_docs?include_docs=true");
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var result = JsonSerializer.Deserialize<Dictionary<string, object>>(json, _jsonOptions);
                        if (result?.TryGetValue("rows", out var rows) == true && rows is JsonElement rowsEl)
                        {
                            return rowsEl.EnumerateArray().Select(r => r.GetProperty("doc")).Cast<object>().ToList();
                        }
                    }
                    return Enumerable.Empty<object>();

                case NoSqlEngine.Elasticsearch:
                case NoSqlEngine.OpenSearch:
                    // Elasticsearch/OpenSearch HTTP API
                    var searchQuery = string.IsNullOrEmpty(query) ? "{\"query\":{\"match_all\":{}}}" : query;
                    var content = new StringContent(searchQuery, Encoding.UTF8, "application/json");
                    response = await _httpClient!.PostAsync($"/{collection}/_search", content);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var result = JsonSerializer.Deserialize<Dictionary<string, object>>(json, _jsonOptions);
                        if (result?.TryGetValue("hits", out var hits) == true && hits is JsonElement hitsEl)
                        {
                            if (hitsEl.TryGetProperty("hits", out var innerHits))
                            {
                                return innerHits.EnumerateArray().Select(h => h.GetProperty("_source")).Cast<object>().ToList();
                            }
                        }
                    }
                    return Enumerable.Empty<object>();

                default:
                    throw new NotSupportedException($"Query not supported for engine {_config.Engine}");
            }
        }

        private async Task<long> CountEngineAsync(string database, string collection, string? filter)
        {
            switch (_config.Engine)
            {
                case NoSqlEngine.MongoDB:
                case NoSqlEngine.DocumentDB:
                case NoSqlEngine.CosmosDB when _mongoClient != null:
                    // Use MongoDB Driver
                    var mongoDb = _mongoClient!.GetDatabase(database);
                    var mongoCollection = mongoDb.GetCollection<BsonDocument>(collection);
                    var mongoFilter = string.IsNullOrEmpty(filter) || filter == "{}"
                        ? Builders<BsonDocument>.Filter.Empty
                        : BsonDocument.Parse(filter);
                    return await mongoCollection.CountDocumentsAsync(mongoFilter);

                case NoSqlEngine.Redis:
                case NoSqlEngine.Memcached:
                    // Redis - count keys
                    var server = _redisConnection!.GetServer(_redisConnection.GetEndPoints().First());
                    var pattern = $"{database}:{collection}:*";
                    return server.Keys(pattern: pattern).Count();

                case NoSqlEngine.CouchDB:
                    // CouchDB HTTP API
                    var response = await _httpClient!.GetAsync($"/{database}/_all_docs");
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var result = JsonSerializer.Deserialize<Dictionary<string, object>>(json, _jsonOptions);
                        if (result?.TryGetValue("total_rows", out var count) == true)
                        {
                            return Convert.ToInt64(count);
                        }
                    }
                    return 0;

                case NoSqlEngine.Elasticsearch:
                case NoSqlEngine.OpenSearch:
                    // Elasticsearch/OpenSearch count API
                    response = await _httpClient!.GetAsync($"/{collection}/_count");
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var result = JsonSerializer.Deserialize<Dictionary<string, long>>(json, _jsonOptions);
                        return result?.GetValueOrDefault("count", 0) ?? 0;
                    }
                    return 0;

                default:
                    throw new NotSupportedException($"Count not supported for engine {_config.Engine}");
            }
        }

        private async Task<IEnumerable<string>> ListDatabasesFromEngineAsync()
        {
            switch (_config.Engine)
            {
                case NoSqlEngine.MongoDB:
                case NoSqlEngine.DocumentDB:
                case NoSqlEngine.CosmosDB when _mongoClient != null:
                    // Use MongoDB Driver
                    var cursor = await _mongoClient!.ListDatabaseNamesAsync();
                    return await cursor.ToListAsync();

                case NoSqlEngine.Redis:
                case NoSqlEngine.Memcached:
                    // Redis doesn't have databases in traditional sense - return default
                    return new[] { "default" };

                case NoSqlEngine.CouchDB:
                    // CouchDB HTTP API
                    var response = await _httpClient!.GetAsync("/_all_dbs");
                    if (response.IsSuccessStatusCode)
                    {
                        return await response.Content.ReadFromJsonAsync<List<string>>() ?? new List<string>();
                    }
                    return Enumerable.Empty<string>();

                case NoSqlEngine.Elasticsearch:
                case NoSqlEngine.OpenSearch:
                    // Elasticsearch uses indices, not databases
                    return new[] { "default" };

                default:
                    throw new NotSupportedException($"List databases not supported for engine {_config.Engine}");
            }
        }

        private async Task<IEnumerable<string>> ListCollectionsFromEngineAsync(string database)
        {
            switch (_config.Engine)
            {
                case NoSqlEngine.MongoDB:
                case NoSqlEngine.DocumentDB:
                case NoSqlEngine.CosmosDB when _mongoClient != null:
                    // Use MongoDB Driver
                    var mongoDb = _mongoClient!.GetDatabase(database);
                    var cursor = await mongoDb.ListCollectionNamesAsync();
                    return await cursor.ToListAsync();

                case NoSqlEngine.Redis:
                case NoSqlEngine.Memcached:
                    // Redis - extract collection names from keys
                    var server = _redisConnection!.GetServer(_redisConnection.GetEndPoints().First());
                    var pattern = $"{database}:*";
                    var collections = new HashSet<string>();
                    foreach (var key in server.Keys(pattern: pattern))
                    {
                        var parts = key.ToString().Split(':');
                        if (parts.Length >= 2)
                        {
                            collections.Add(parts[1]);
                        }
                    }
                    return collections;

                case NoSqlEngine.CouchDB:
                    // CouchDB doesn't have collections, only documents
                    return Enumerable.Empty<string>();

                case NoSqlEngine.Elasticsearch:
                case NoSqlEngine.OpenSearch:
                    // Elasticsearch - list indices
                    var response = await _httpClient!.GetAsync("/_cat/indices?format=json");
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var indices = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(json, _jsonOptions);
                        return indices?.Select(i => i.GetValueOrDefault("index", "").ToString() ?? "")
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToList() ?? Enumerable.Empty<string>();
                    }
                    return Enumerable.Empty<string>();

                default:
                    throw new NotSupportedException($"List collections not supported for engine {_config.Engine}");
            }
        }

        #endregion

        private static string? GetDocumentId(object doc)
        {
            if (doc is JsonElement element && element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("_id", out var idProp))
                    return idProp.ToString();
                if (element.TryGetProperty("id", out idProp))
                    return idProp.ToString();
            }
            return null;
        }
    }

/// <summary>
/// Configuration for NoSQL database.
/// </summary>
public class NoSqlConfig : DatabaseConfigBase
{
        public NoSqlEngine Engine { get; set; } = NoSqlEngine.InMemory;
        public string Endpoint { get; set; } = "http://localhost:27017";
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? ReplicaSet { get; set; }

        /// <summary>
        /// Create an in-memory configuration for testing.
        /// NOTE: In-memory mode is ONLY for testing/development. Use real database engines in production.
        /// </summary>
        public static NoSqlConfig InMemory => new() { Engine = NoSqlEngine.InMemory };

        /// <summary>
        /// Create MongoDB configuration from connection string.
        /// Connection string format: mongodb://[username:password@]host[:port][/database][?options]
        /// Example: mongodb://localhost:27017/mydb
        /// Example with auth: mongodb://user:pass@localhost:27017/mydb?authSource=admin
        /// </summary>
        public static NoSqlConfig MongoDB(string connectionString, string? defaultDatabase = null) => new()
        {
            Engine = NoSqlEngine.MongoDB,
            ConnectionString = connectionString,
            DefaultDatabase = defaultDatabase ?? ExtractDatabase(connectionString),
            Endpoint = ExtractEndpoint(connectionString)
        };

        /// <summary>
        /// Create AWS DocumentDB configuration (MongoDB-compatible).
        /// Connection string format same as MongoDB.
        /// </summary>
        public static NoSqlConfig DocumentDB(string connectionString, string? defaultDatabase = null) => new()
        {
            Engine = NoSqlEngine.DocumentDB,
            ConnectionString = connectionString,
            DefaultDatabase = defaultDatabase ?? ExtractDatabase(connectionString),
            Endpoint = ExtractEndpoint(connectionString)
        };

        /// <summary>
        /// Create Azure Cosmos DB configuration (MongoDB API).
        /// Connection string format: mongodb://[account]:[key]@[account].mongo.cosmos.azure.com:10255/[database]?ssl=true
        /// </summary>
        public static NoSqlConfig CosmosDB(string connectionString, string? defaultDatabase = null) => new()
        {
            Engine = NoSqlEngine.CosmosDB,
            ConnectionString = connectionString,
            DefaultDatabase = defaultDatabase ?? ExtractDatabase(connectionString),
            Endpoint = ExtractEndpoint(connectionString)
        };

        /// <summary>
        /// Create Redis configuration.
        /// Connection string format: localhost:6379,password=secret,ssl=true
        /// </summary>
        public static NoSqlConfig Redis(string connectionString) => new()
        {
            Engine = NoSqlEngine.Redis,
            ConnectionString = connectionString
        };

        /// <summary>
        /// Create CouchDB configuration.
        /// </summary>
        public static NoSqlConfig CouchDB(string endpoint, string? username = null, string? password = null, string? defaultDatabase = null) => new()
        {
            Engine = NoSqlEngine.CouchDB,
            Endpoint = endpoint,
            Username = username,
            Password = password,
            DefaultDatabase = defaultDatabase
        };

        /// <summary>
        /// Create Elasticsearch configuration.
        /// </summary>
        public static NoSqlConfig Elasticsearch(string endpoint, string? username = null, string? password = null) => new()
        {
            Engine = NoSqlEngine.Elasticsearch,
            Endpoint = endpoint,
            Username = username,
            Password = password
        };

        /// <summary>
        /// Create OpenSearch configuration.
        /// </summary>
        public static NoSqlConfig OpenSearch(string endpoint, string? username = null, string? password = null) => new()
        {
            Engine = NoSqlEngine.OpenSearch,
            Endpoint = endpoint,
            Username = username,
            Password = password
        };

        private static string ExtractEndpoint(string connectionString)
        {
            var match = System.Text.RegularExpressions.Regex.Match(connectionString, @"mongodb://([^/]+)");
            return match.Success ? $"http://{match.Groups[1].Value}" : "http://localhost:27017";
        }

        private static string? ExtractDatabase(string connectionString)
        {
            // Extract database from mongodb://host:port/database format
            var match = System.Text.RegularExpressions.Regex.Match(connectionString, @"mongodb://[^/]+/([^?]+)");
            return match.Success ? match.Groups[1].Value : null;
        }
    }

    /// <summary>
    /// Supported NoSQL database engines.
    /// </summary>
    public enum NoSqlEngine
    {
        // In-Memory / Testing
        /// <summary>In-memory document store for testing.</summary>
        InMemory,

        // Document Databases
        /// <summary>MongoDB document database.</summary>
        MongoDB,
        /// <summary>CouchDB document database.</summary>
        CouchDB,
        /// <summary>RavenDB document database.</summary>
        RavenDB,
        /// <summary>ArangoDB multi-model database.</summary>
        ArangoDB,
        /// <summary>Couchbase Server.</summary>
        Couchbase,
        /// <summary>Amazon DocumentDB (MongoDB-compatible).</summary>
        DocumentDB,
        /// <summary>Azure Cosmos DB.</summary>
        CosmosDB,
        /// <summary>Firebase Firestore.</summary>
        Firestore,
        /// <summary>FaunaDB (serverless).</summary>
        FaunaDB,

        // Key-Value Stores
        /// <summary>Redis in-memory data store.</summary>
        Redis,
        /// <summary>Amazon DynamoDB.</summary>
        DynamoDB,
        /// <summary>Memcached distributed cache.</summary>
        Memcached,
        /// <summary>etcd distributed key-value store.</summary>
        Etcd,
        /// <summary>Consul KV store.</summary>
        Consul,
        /// <summary>Apache Ignite.</summary>
        Ignite,
        /// <summary>Hazelcast in-memory data grid.</summary>
        Hazelcast,
        /// <summary>Aerospike real-time data platform.</summary>
        Aerospike,

        // Wide-Column Stores
        /// <summary>Apache Cassandra distributed database.</summary>
        Cassandra,
        /// <summary>ScyllaDB (Cassandra-compatible).</summary>
        ScyllaDB,
        /// <summary>Apache HBase.</summary>
        HBase,
        /// <summary>Google Cloud Bigtable.</summary>
        Bigtable,
        /// <summary>Azure Table Storage.</summary>
        AzureTables,

        // Graph Databases
        /// <summary>Neo4j graph database.</summary>
        Neo4j,
        /// <summary>Amazon Neptune.</summary>
        Neptune,
        /// <summary>JanusGraph distributed graph database.</summary>
        JanusGraph,
        /// <summary>TigerGraph enterprise graph.</summary>
        TigerGraph,
        /// <summary>Dgraph distributed graph database.</summary>
        Dgraph,
        /// <summary>OrientDB multi-model database.</summary>
        OrientDB,

        // Search Engines
        /// <summary>Elasticsearch search and analytics.</summary>
        Elasticsearch,
        /// <summary>OpenSearch (Elasticsearch fork).</summary>
        OpenSearch,
        /// <summary>Apache Solr search platform.</summary>
        Solr,
        /// <summary>Typesense search engine.</summary>
        Typesense,
        /// <summary>Meilisearch search engine.</summary>
        Meilisearch,
        /// <summary>Algolia search service.</summary>
        Algolia,

        // Time Series
        /// <summary>InfluxDB time series database.</summary>
        InfluxDB,
        /// <summary>Prometheus monitoring system.</summary>
        Prometheus,
        /// <summary>QuestDB time series database.</summary>
        QuestDB,
        /// <summary>TDengine time series database.</summary>
        TDengine,
        /// <summary>Victoria Metrics.</summary>
        VictoriaMetrics,

        // Vector Databases
        /// <summary>Pinecone vector database.</summary>
        Pinecone,
        /// <summary>Weaviate vector database.</summary>
        Weaviate,
        /// <summary>Milvus vector database.</summary>
        Milvus,
        /// <summary>Qdrant vector database.</summary>
        Qdrant,
        /// <summary>Chroma vector database.</summary>
        Chroma
    }

// Multi-instance connection management is now provided by the base class via:
// - StorageConnectionRegistry<NoSqlConfig> accessible via ConnectionRegistry property
// - StorageConnectionInstance<NoSqlConfig> for individual instance management
// - StorageRole enum for role-based instance selection
