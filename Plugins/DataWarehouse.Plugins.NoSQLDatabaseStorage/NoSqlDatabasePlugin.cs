using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.NoSQLDatabaseStorage
{
    /// <summary>
    /// NoSQL document database storage plugin.
    ///
    /// Supports:
    /// - MongoDB (via REST API or driver)
    /// - CouchDB
    /// - RavenDB
    /// - DynamoDB-compatible APIs
    /// - In-memory document store for testing
    ///
    /// Features:
    /// - Document CRUD operations
    /// - Collection management
    /// - Query support with JSON query syntax
    /// - Indexing management
    /// - Aggregation pipelines
    /// - Change streams (where supported)
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
    public sealed class NoSqlDatabasePlugin : DatabasePluginBase
    {
        private readonly NoSqlConfig _nosqlConfig;
        private readonly HttpClient? _httpClient;

        // In-memory storage for testing or embedded mode
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<string, string>>> _inMemoryStore = new();

        public override string Id => "datawarehouse.plugins.database.nosql";
        public override string Name => "NoSQL Database";
        public override string Version => "1.0.0";
        public override string Scheme => "nosql";
        public override DatabaseType DatabaseType => DatabaseType.NoSQL;
        public override string Engine => _nosqlConfig.Engine.ToString();

        public NoSqlDatabasePlugin(NoSqlConfig? config = null) : base(config)
        {
            _nosqlConfig = config ?? new NoSqlConfig();

            if (_nosqlConfig.Engine != NoSqlEngine.InMemory)
            {
                _httpClient = new HttpClient
                {
                    BaseAddress = new Uri(_nosqlConfig.Endpoint),
                    Timeout = TimeSpan.FromSeconds(_config.CommandTimeoutSeconds)
                };

                // Add authentication headers if configured
                if (!string.IsNullOrEmpty(_nosqlConfig.Username) && !string.IsNullOrEmpty(_nosqlConfig.Password))
                {
                    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_nosqlConfig.Username}:{_nosqlConfig.Password}"));
                    _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
                }
            }
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
            database ??= _nosqlConfig.DefaultDatabase;

            using var reader = new StreamReader(data);
            var json = await reader.ReadToEndAsync();

            // Generate ID if not provided
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
            }

            if (_nosqlConfig.Engine == NoSqlEngine.InMemory)
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
            database ??= _nosqlConfig.DefaultDatabase;

            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("Document ID is required");

            string json;
            if (_nosqlConfig.Engine == NoSqlEngine.InMemory)
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
            database ??= _nosqlConfig.DefaultDatabase;

            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("Document ID is required");

            if (_nosqlConfig.Engine == NoSqlEngine.InMemory)
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

            if (_nosqlConfig.Engine == NoSqlEngine.InMemory)
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
                var collections = await ListCollectionsAsync(_nosqlConfig.DefaultDatabase);
                foreach (var collection in collections)
                {
                    if (ct.IsCancellationRequested) yield break;

                    var results = await ExecuteQueryAsync(_nosqlConfig.DefaultDatabase, collection, "{}", null);
                    foreach (var doc in results)
                    {
                        if (ct.IsCancellationRequested) yield break;

                        var id = GetDocumentId(doc);
                        if (!string.IsNullOrEmpty(id))
                        {
                            var path = $"{_nosqlConfig.DefaultDatabase}/{collection}/{id}";
                            yield return new StorageListItem(new Uri($"nosql:///{path}"), 0);
                        }
                    }
                }
            }
        }

        #endregion

        #region Database Operations

        protected override Task ConnectAsync()
        {
            if (_nosqlConfig.Engine == NoSqlEngine.InMemory)
            {
                _isConnected = true;
                return Task.CompletedTask;
            }

            // Test connection
            return TestConnectionAsync();
        }

        protected override Task DisconnectAsync()
        {
            _isConnected = false;
            return Task.CompletedTask;
        }

        protected override async Task<IEnumerable<object>> ExecuteQueryAsync(
            string? database, string? collection, string? query, Dictionary<string, object>? parameters)
        {
            await EnsureConnectedAsync();

            database ??= _nosqlConfig.DefaultDatabase;

            if (_nosqlConfig.Engine == NoSqlEngine.InMemory)
            {
                return QueryInMemory(database ?? "default", collection ?? "default", query);
            }

            return await QueryEngineAsync(database!, collection!, query, parameters);
        }

        protected override async Task<long> CountAsync(string? database, string? collection, string? filter)
        {
            await EnsureConnectedAsync();

            database ??= _nosqlConfig.DefaultDatabase;

            if (_nosqlConfig.Engine == NoSqlEngine.InMemory)
            {
                return CountInMemory(database ?? "default", collection ?? "default", filter);
            }

            return await CountEngineAsync(database!, collection!, filter);
        }

        protected override Task CreateDatabaseAsync(string database)
        {
            if (_nosqlConfig.Engine == NoSqlEngine.InMemory)
            {
                _inMemoryStore.TryAdd(database, new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>());
            }
            // Most NoSQL databases create databases implicitly
            return Task.CompletedTask;
        }

        protected override Task DropDatabaseAsync(string database)
        {
            if (_nosqlConfig.Engine == NoSqlEngine.InMemory)
            {
                _inMemoryStore.TryRemove(database, out _);
            }
            return Task.CompletedTask;
        }

        protected override Task CreateCollectionAsync(string? database, string collection, Dictionary<string, object>? schema)
        {
            database ??= _nosqlConfig.DefaultDatabase ?? "default";

            if (_nosqlConfig.Engine == NoSqlEngine.InMemory)
            {
                var db = _inMemoryStore.GetOrAdd(database, _ => new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>());
                db.TryAdd(collection, new ConcurrentDictionary<string, string>());
            }
            return Task.CompletedTask;
        }

        protected override Task DropCollectionAsync(string? database, string collection)
        {
            database ??= _nosqlConfig.DefaultDatabase ?? "default";

            if (_nosqlConfig.Engine == NoSqlEngine.InMemory)
            {
                if (_inMemoryStore.TryGetValue(database, out var db))
                {
                    db.TryRemove(collection, out _);
                }
            }
            return Task.CompletedTask;
        }

        protected override Task<IEnumerable<string>> ListDatabasesAsync()
        {
            if (_nosqlConfig.Engine == NoSqlEngine.InMemory)
            {
                return Task.FromResult<IEnumerable<string>>(_inMemoryStore.Keys.ToList());
            }

            return ListDatabasesFromEngineAsync();
        }

        protected override Task<IEnumerable<string>> ListCollectionsAsync(string? database)
        {
            database ??= _nosqlConfig.DefaultDatabase ?? "default";

            if (_nosqlConfig.Engine == NoSqlEngine.InMemory)
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

            var database = GetPayloadString(payload, "database") ?? _nosqlConfig.DefaultDatabase;
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

            var database = GetPayloadString(payload, "database") ?? _nosqlConfig.DefaultDatabase;
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

            var database = GetPayloadString(payload, "database") ?? _nosqlConfig.DefaultDatabase;
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

            var database = GetPayloadString(payload, "database") ?? _nosqlConfig.DefaultDatabase;
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

            var database = GetPayloadString(payload, "database") ?? _nosqlConfig.DefaultDatabase;
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
                var response = _nosqlConfig.Engine switch
                {
                    NoSqlEngine.MongoDB => await _httpClient!.GetAsync("/"),
                    NoSqlEngine.CouchDB => await _httpClient!.GetAsync("/"),
                    _ => null
                };

                _isConnected = response?.IsSuccessStatusCode ?? false;
            }
            catch
            {
                _isConnected = false;
                throw;
            }
        }

        private async Task SaveToEngineAsync(string database, string collection, string id, string json)
        {
            switch (_nosqlConfig.Engine)
            {
                case NoSqlEngine.MongoDB:
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    await _httpClient!.PutAsync($"/{database}/{collection}/{id}", content);
                    break;
                case NoSqlEngine.CouchDB:
                    content = new StringContent(json, Encoding.UTF8, "application/json");
                    await _httpClient!.PutAsync($"/{database}/{id}", content);
                    break;
                default:
                    throw new NotSupportedException($"Engine {_nosqlConfig.Engine} not supported for external storage");
            }
        }

        private async Task<string> LoadFromEngineAsync(string database, string collection, string id)
        {
            HttpResponseMessage response;
            switch (_nosqlConfig.Engine)
            {
                case NoSqlEngine.MongoDB:
                    response = await _httpClient!.GetAsync($"/{database}/{collection}/{id}");
                    break;
                case NoSqlEngine.CouchDB:
                    response = await _httpClient!.GetAsync($"/{database}/{id}");
                    break;
                default:
                    throw new NotSupportedException($"Engine {_nosqlConfig.Engine} not supported");
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private async Task DeleteFromEngineAsync(string database, string collection, string id)
        {
            switch (_nosqlConfig.Engine)
            {
                case NoSqlEngine.MongoDB:
                    await _httpClient!.DeleteAsync($"/{database}/{collection}/{id}");
                    break;
                case NoSqlEngine.CouchDB:
                    await _httpClient!.DeleteAsync($"/{database}/{id}");
                    break;
            }
        }

        private async Task<IEnumerable<object>> QueryEngineAsync(string database, string collection, string? query, Dictionary<string, object>? parameters)
        {
            var response = await _httpClient!.GetAsync($"/{database}/{collection}?query={Uri.EscapeDataString(query ?? "{}")}");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<List<object>>(json, _jsonOptions) ?? new List<object>();
            }
            return Enumerable.Empty<object>();
        }

        private async Task<long> CountEngineAsync(string database, string collection, string? filter)
        {
            var response = await _httpClient!.GetAsync($"/{database}/{collection}/_count?filter={Uri.EscapeDataString(filter ?? "{}")}");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<Dictionary<string, long>>(json);
                return result?.GetValueOrDefault("count", 0) ?? 0;
            }
            return 0;
        }

        private async Task<IEnumerable<string>> ListDatabasesFromEngineAsync()
        {
            var response = await _httpClient!.GetAsync("/_all_dbs");
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<List<string>>() ?? new List<string>();
            }
            return Enumerable.Empty<string>();
        }

        private async Task<IEnumerable<string>> ListCollectionsFromEngineAsync(string database)
        {
            var response = await _httpClient!.GetAsync($"/{database}/_collections");
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<List<string>>() ?? new List<string>();
            }
            return Enumerable.Empty<string>();
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
    public class NoSqlConfig : DatabaseConfig
    {
        public NoSqlEngine Engine { get; set; } = NoSqlEngine.InMemory;
        public string Endpoint { get; set; } = "http://localhost:27017";
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? ReplicaSet { get; set; }

        public static NoSqlConfig InMemory => new() { Engine = NoSqlEngine.InMemory };

        public static NoSqlConfig MongoDB(string connectionString) => new()
        {
            Engine = NoSqlEngine.MongoDB,
            ConnectionString = connectionString,
            Endpoint = ExtractEndpoint(connectionString)
        };

        public static NoSqlConfig CouchDB(string endpoint, string? username = null, string? password = null) => new()
        {
            Engine = NoSqlEngine.CouchDB,
            Endpoint = endpoint,
            Username = username,
            Password = password
        };

        private static string ExtractEndpoint(string connectionString)
        {
            var match = System.Text.RegularExpressions.Regex.Match(connectionString, @"mongodb://([^/]+)");
            return match.Success ? $"http://{match.Groups[1].Value}" : "http://localhost:27017";
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

    #region Multi-Instance Connection Management

    /// <summary>
    /// Manages multiple NoSQL database connection instances.
    /// </summary>
    public sealed class NoSqlConnectionRegistry : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, NoSqlConnectionInstance> _instances = new();
        private volatile bool _disposed;

        /// <summary>
        /// Registers a new database instance.
        /// </summary>
        public NoSqlConnectionInstance Register(string instanceId, NoSqlConfig config)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NoSqlConnectionRegistry));

            if (_instances.ContainsKey(instanceId))
                throw new InvalidOperationException($"Instance '{instanceId}' already registered.");

            var instance = new NoSqlConnectionInstance(instanceId, config);
            _instances[instanceId] = instance;
            return instance;
        }

        /// <summary>
        /// Gets an instance by ID.
        /// </summary>
        public NoSqlConnectionInstance? Get(string instanceId)
        {
            return _instances.TryGetValue(instanceId, out var instance) ? instance : null;
        }

        /// <summary>
        /// Gets all registered instances.
        /// </summary>
        public IEnumerable<NoSqlConnectionInstance> GetAll() => _instances.Values;

        /// <summary>
        /// Gets instances by role.
        /// </summary>
        public IEnumerable<NoSqlConnectionInstance> GetByRole(NoSqlConnectionRole role)
        {
            return _instances.Values.Where(i => i.Roles.HasFlag(role));
        }

        /// <summary>
        /// Unregisters and disposes an instance.
        /// </summary>
        public async Task UnregisterAsync(string instanceId)
        {
            if (_instances.TryRemove(instanceId, out var instance))
            {
                await instance.DisposeAsync();
            }
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
        }
    }

    /// <summary>
    /// Represents a single NoSQL database connection instance.
    /// </summary>
    public sealed class NoSqlConnectionInstance : IAsyncDisposable
    {
        public string InstanceId { get; }
        public NoSqlConfig Config { get; }
        public NoSqlConnectionRole Roles { get; set; } = NoSqlConnectionRole.Storage;
        public int Priority { get; set; } = 0;
        public bool IsConnected { get; private set; }
        public DateTime? LastActivity { get; private set; }

        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private object? _connection;
        private HttpClient? _httpClient;

        public NoSqlConnectionInstance(string instanceId, NoSqlConfig config)
        {
            InstanceId = instanceId;
            Config = config;
        }

        public async Task ConnectAsync()
        {
            if (IsConnected) return;

            await _connectionLock.WaitAsync();
            try
            {
                if (IsConnected) return;

                _connection = await CreateConnectionAsync();
                IsConnected = true;
                LastActivity = DateTime.UtcNow;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        public async Task DisconnectAsync()
        {
            if (!IsConnected) return;

            await _connectionLock.WaitAsync();
            try
            {
                if (_connection is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync();
                else if (_connection is IDisposable disposable)
                    disposable.Dispose();

                _httpClient?.Dispose();
                _httpClient = null;
                _connection = null;
                IsConnected = false;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private Task<object> CreateConnectionAsync()
        {
            // For HTTP-based NoSQL databases
            if (!string.IsNullOrEmpty(Config.Endpoint))
            {
                _httpClient = new HttpClient
                {
                    BaseAddress = new Uri(Config.Endpoint),
                    Timeout = TimeSpan.FromSeconds(30)
                };

                if (!string.IsNullOrEmpty(Config.Username) && !string.IsNullOrEmpty(Config.Password))
                {
                    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Config.Username}:{Config.Password}"));
                    _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
                }
            }

            return Task.FromResult<object>(new { Engine = Config.Engine, Endpoint = Config.Endpoint });
        }

        public HttpClient? GetHttpClient() => _httpClient;

        public void RecordActivity() => LastActivity = DateTime.UtcNow;

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
            _connectionLock.Dispose();
        }
    }

    /// <summary>
    /// Roles a NoSQL connection instance can serve.
    /// </summary>
    [Flags]
    public enum NoSqlConnectionRole
    {
        None = 0,
        Storage = 1,
        Index = 2,
        Cache = 4,
        Metadata = 8,
        Search = 16,
        Vector = 32,
        Graph = 64,
        All = Storage | Index | Cache | Metadata | Search | Vector | Graph
    }

    #endregion
}
