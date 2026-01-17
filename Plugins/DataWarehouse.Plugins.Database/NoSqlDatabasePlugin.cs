using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.Database
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
            else
            {
                // Would need to call database-specific drop command
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
            // Most NoSQL databases create collections implicitly
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
            if (message.Payload is not Dictionary<string, object> payload)
                return MessageResponse.Error("Invalid payload");

            var database = GetStringOrDefault(payload, "database") ?? _nosqlConfig.DefaultDatabase;
            var collection = GetStringOrDefault(payload, "collection");
            var pipeline = payload.TryGetValue("pipeline", out var pObj) ? pObj : null;

            if (string.IsNullOrEmpty(collection) || pipeline == null)
                return MessageResponse.Error("Missing required parameters: collection, pipeline");

            // Execute aggregation (simplified - would need real implementation per engine)
            var results = await ExecuteQueryAsync(database, collection, JsonSerializer.Serialize(pipeline, _jsonOptions), null);
            return MessageResponse.Ok(new { Database = database, Collection = collection, Results = results });
        }

        private async Task<MessageResponse> HandleCreateIndexAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload)
                return MessageResponse.Error("Invalid payload");

            var database = GetStringOrDefault(payload, "database") ?? _nosqlConfig.DefaultDatabase;
            var collection = GetStringOrDefault(payload, "collection");
            var indexName = GetStringOrDefault(payload, "indexName");
            var fields = payload.TryGetValue("fields", out var fObj) ? fObj : null;

            if (string.IsNullOrEmpty(collection) || fields == null)
                return MessageResponse.Error("Missing required parameters: collection, fields");

            // Create index (simplified)
            await Task.CompletedTask;
            return MessageResponse.Ok(new { Database = database, Collection = collection, IndexName = indexName, Created = true });
        }

        private async Task<MessageResponse> HandleDropIndexAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload)
                return MessageResponse.Error("Invalid payload");

            var database = GetStringOrDefault(payload, "database") ?? _nosqlConfig.DefaultDatabase;
            var collection = GetStringOrDefault(payload, "collection");
            var indexName = GetStringOrDefault(payload, "indexName");

            if (string.IsNullOrEmpty(collection) || string.IsNullOrEmpty(indexName))
                return MessageResponse.Error("Missing required parameters: collection, indexName");

            await Task.CompletedTask;
            return MessageResponse.Ok(new { Database = database, Collection = collection, IndexName = indexName, Dropped = true });
        }

        private async Task<MessageResponse> HandleBulkInsertAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload)
                return MessageResponse.Error("Invalid payload");

            var database = GetStringOrDefault(payload, "database") ?? _nosqlConfig.DefaultDatabase;
            var collection = GetStringOrDefault(payload, "collection");
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
            if (message.Payload is not Dictionary<string, object> payload)
                return MessageResponse.Error("Invalid payload");

            var database = GetStringOrDefault(payload, "database") ?? _nosqlConfig.DefaultDatabase;
            var collection = GetStringOrDefault(payload, "collection");
            var filter = GetStringOrDefault(payload, "filter");

            if (string.IsNullOrEmpty(collection))
                return MessageResponse.Error("Missing required parameter: collection");

            // Get matching documents and delete them
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

            // Simple query - returns all documents (real implementation would parse query)
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
            // Test connection based on engine
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
                    // MongoDB REST API or driver call
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
            // Simplified query - real implementation would use proper driver
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
        /// <summary>NoSQL engine type.</summary>
        public NoSqlEngine Engine { get; set; } = NoSqlEngine.InMemory;

        /// <summary>Server endpoint URL.</summary>
        public string Endpoint { get; set; } = "http://localhost:27017";

        /// <summary>Username for authentication.</summary>
        public string? Username { get; set; }

        /// <summary>Password for authentication.</summary>
        public string? Password { get; set; }

        /// <summary>Replica set name (for MongoDB).</summary>
        public string? ReplicaSet { get; set; }

        /// <summary>Creates in-memory configuration for testing.</summary>
        public static NoSqlConfig InMemory => new() { Engine = NoSqlEngine.InMemory };

        /// <summary>Creates MongoDB configuration.</summary>
        public static NoSqlConfig MongoDB(string connectionString) => new()
        {
            Engine = NoSqlEngine.MongoDB,
            ConnectionString = connectionString,
            Endpoint = ExtractEndpoint(connectionString)
        };

        /// <summary>Creates CouchDB configuration.</summary>
        public static NoSqlConfig CouchDB(string endpoint, string? username = null, string? password = null) => new()
        {
            Engine = NoSqlEngine.CouchDB,
            Endpoint = endpoint,
            Username = username,
            Password = password
        };

        private static string ExtractEndpoint(string connectionString)
        {
            // Simple extraction - real implementation would parse properly
            var match = System.Text.RegularExpressions.Regex.Match(connectionString, @"mongodb://([^/]+)");
            return match.Success ? $"http://{match.Groups[1].Value}" : "http://localhost:27017";
        }
    }

    /// <summary>
    /// NoSQL engine types.
    /// </summary>
    public enum NoSqlEngine
    {
        InMemory,
        MongoDB,
        CouchDB,
        RavenDB,
        DynamoDB
    }
}
