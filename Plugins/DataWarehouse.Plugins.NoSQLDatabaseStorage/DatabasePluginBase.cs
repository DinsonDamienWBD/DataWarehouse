using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.NoSQLDatabaseStorage
{
    /// <summary>
    /// Base class for database storage plugins, extending ListableStoragePluginBase.
    ///
    /// URI format: {scheme}://{database}/{collection_or_table}/{document_id}
    /// Example: mongodb://mydb/users/12345
    ///          sqlite://data.db/customers/cust_001
    ///          mysql://inventory/products/SKU123
    ///
    /// This base class provides:
    /// - Common database operations mapped to storage operations
    /// - JSON serialization/deserialization for documents
    /// - Transaction support via message handlers
    /// - Query capabilities via message handlers
    /// - Schema management via message handlers
    /// - Connection pooling management
    /// </summary>
    public abstract class DatabasePluginBase : ListableStoragePluginBase
    {
        protected readonly DatabaseConfig _config;
        protected readonly JsonSerializerOptions _jsonOptions;
        protected bool _isConnected;
        protected readonly SemaphoreSlim _connectionLock = new(1, 1);

        /// <summary>
        /// The type of database (NoSQL, Embedded, Relational).
        /// </summary>
        public abstract DatabaseType DatabaseType { get; }

        /// <summary>
        /// The specific database engine (MongoDB, SQLite, MySQL, etc.).
        /// </summary>
        public abstract string Engine { get; }

        /// <summary>
        /// Whether the connection is currently active.
        /// </summary>
        public bool IsConnected => _isConnected;

        protected DatabasePluginBase(DatabaseConfig? config = null)
        {
            _config = config ?? new DatabaseConfig();
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["DatabaseType"] = DatabaseType.ToString();
            metadata["Engine"] = Engine;
            metadata["ConnectionString"] = _config.ConnectionString != null ? "***" : "not configured";
            metadata["IsConnected"] = _isConnected;
            metadata["SupportsTransactions"] = SupportsTransactions;
            metadata["SupportsQueries"] = true;
            metadata["SupportsConcurrency"] = true;
            metadata["SupportsListing"] = true;
            return metadata;
        }

        /// <summary>
        /// Whether this database supports transactions.
        /// </summary>
        protected virtual bool SupportsTransactions => true;

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
                new() { Name = $"{prefix}.listcollections", DisplayName = "List Collections", Description = "List collections in database" }
            ];
        }

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
                _ => null
            };
        }

        #region Message Handlers

        protected virtual async Task<MessageResponse> HandleSaveAsync(PluginMessage message)
        {
            var payload = message.Payload;

            var database = GetPayloadString(payload, "database");
            var collection = GetPayloadString(payload, "collection");
            var id = GetPayloadString(payload, "id");
            var data = payload.TryGetValue("data", out var dataObj) ? dataObj : null;

            if (string.IsNullOrEmpty(collection) || data == null)
                return MessageResponse.Error("Missing required parameters: collection, data");

            var uri = BuildUri(database, collection, id);
            var jsonData = JsonSerializer.Serialize(data, _jsonOptions);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonData));

            await SaveAsync(uri, stream);
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
            var parameters = GetPayloadDictionary(payload, "parameters");

            var results = await ExecuteQueryAsync(database, collection, query, parameters);
            return MessageResponse.Ok(new { Database = database, Collection = collection, Results = results });
        }

        protected virtual async Task<MessageResponse> HandleCountAsync(PluginMessage message)
        {
            var payload = message.Payload;

            var database = GetPayloadString(payload, "database");
            var collection = GetPayloadString(payload, "collection");
            var filter = GetPayloadString(payload, "filter");

            var count = await CountAsync(database, collection, filter);
            return MessageResponse.Ok(new { Database = database, Collection = collection, Count = count });
        }

        protected virtual async Task<MessageResponse> HandleConnectAsync(PluginMessage message)
        {
            await ConnectAsync();
            return MessageResponse.Ok(new { Connected = _isConnected });
        }

        protected virtual async Task<MessageResponse> HandleDisconnectAsync(PluginMessage message)
        {
            await DisconnectAsync();
            return MessageResponse.Ok(new { Disconnected = true });
        }

        protected virtual async Task<MessageResponse> HandleCreateDatabaseAsync(PluginMessage message)
        {
            var payload = message.Payload;

            var database = GetPayloadString(payload, "database");
            if (string.IsNullOrEmpty(database))
                return MessageResponse.Error("Missing required parameter: database");

            await CreateDatabaseAsync(database);
            return MessageResponse.Ok(new { Database = database, Created = true });
        }

        protected virtual async Task<MessageResponse> HandleDropDatabaseAsync(PluginMessage message)
        {
            var payload = message.Payload;

            var database = GetPayloadString(payload, "database");
            if (string.IsNullOrEmpty(database))
                return MessageResponse.Error("Missing required parameter: database");

            await DropDatabaseAsync(database);
            return MessageResponse.Ok(new { Database = database, Dropped = true });
        }

        protected virtual async Task<MessageResponse> HandleCreateCollectionAsync(PluginMessage message)
        {
            var payload = message.Payload;

            var database = GetPayloadString(payload, "database");
            var collection = GetPayloadString(payload, "collection");
            var schema = GetPayloadDictionary(payload, "schema");

            if (string.IsNullOrEmpty(collection))
                return MessageResponse.Error("Missing required parameter: collection");

            await CreateCollectionAsync(database, collection, schema);
            return MessageResponse.Ok(new { Database = database, Collection = collection, Created = true });
        }

        protected virtual async Task<MessageResponse> HandleDropCollectionAsync(PluginMessage message)
        {
            var payload = message.Payload;

            var database = GetPayloadString(payload, "database");
            var collection = GetPayloadString(payload, "collection");

            if (string.IsNullOrEmpty(collection))
                return MessageResponse.Error("Missing required parameter: collection");

            await DropCollectionAsync(database, collection);
            return MessageResponse.Ok(new { Database = database, Collection = collection, Dropped = true });
        }

        protected virtual async Task<MessageResponse> HandleListDatabasesAsync(PluginMessage message)
        {
            var databases = await ListDatabasesAsync();
            return MessageResponse.Ok(new { Databases = databases });
        }

        protected virtual async Task<MessageResponse> HandleListCollectionsAsync(PluginMessage message)
        {
            var payload = message.Payload;

            var database = GetPayloadString(payload, "database");
            var collections = await ListCollectionsAsync(database);
            return MessageResponse.Ok(new { Database = database, Collections = collections });
        }

        #endregion

        #region Abstract/Virtual Methods

        protected abstract Task ConnectAsync();
        protected abstract Task DisconnectAsync();
        protected abstract Task<IEnumerable<object>> ExecuteQueryAsync(string? database, string? collection, string? query, Dictionary<string, object>? parameters);
        protected abstract Task<long> CountAsync(string? database, string? collection, string? filter);
        protected abstract Task CreateDatabaseAsync(string database);
        protected abstract Task DropDatabaseAsync(string database);
        protected abstract Task CreateCollectionAsync(string? database, string collection, Dictionary<string, object>? schema);
        protected abstract Task DropCollectionAsync(string? database, string collection);
        protected abstract Task<IEnumerable<string>> ListDatabasesAsync();
        protected abstract Task<IEnumerable<string>> ListCollectionsAsync(string? database);

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

        protected static string? GetPayloadString(Dictionary<string, object?> dict, string key)
        {
            return dict.TryGetValue(key, out var value) ? value?.ToString() : null;
        }

        protected static Dictionary<string, object>? GetPayloadDictionary(Dictionary<string, object?> dict, string key)
        {
            if (dict.TryGetValue(key, out var value) && value is Dictionary<string, object> result)
                return result;
            return null;
        }

        protected async Task EnsureConnectedAsync()
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

        #endregion
    }

    public enum DatabaseType { NoSQL, Embedded, Relational }

    public class QueryResult
    {
        public bool Success { get; set; }
        public int RowsAffected { get; set; }
        public long? LastInsertId { get; set; }
        public double ExecutionTimeMs { get; set; }
        public List<string> Columns { get; set; } = new();
        public List<Dictionary<string, object?>> Rows { get; set; } = new();
        public string? ErrorMessage { get; set; }

        public static QueryResult FromRows(List<Dictionary<string, object?>> rows, List<string>? columns = null, double executionTimeMs = 0) => new()
        {
            Success = true,
            Rows = rows,
            Columns = columns ?? (rows.Count > 0 ? rows[0].Keys.ToList() : new List<string>()),
            RowsAffected = rows.Count,
            ExecutionTimeMs = executionTimeMs
        };

        public static QueryResult FromAffected(int rowsAffected, long? lastInsertId = null, double executionTimeMs = 0) => new()
        {
            Success = true,
            RowsAffected = rowsAffected,
            LastInsertId = lastInsertId,
            ExecutionTimeMs = executionTimeMs
        };

        public static QueryResult Error(string message) => new() { Success = false, ErrorMessage = message };
    }

    public class DatabaseConfig
    {
        public string? ConnectionString { get; set; }
        public string? DefaultDatabase { get; set; }
        public int ConnectionTimeoutSeconds { get; set; } = 30;
        public int CommandTimeoutSeconds { get; set; } = 60;
        public int MaxPoolSize { get; set; } = 100;
        public int MinPoolSize { get; set; } = 5;
        public bool EnableRetry { get; set; } = true;
        public int MaxRetries { get; set; } = 3;
    }
}
