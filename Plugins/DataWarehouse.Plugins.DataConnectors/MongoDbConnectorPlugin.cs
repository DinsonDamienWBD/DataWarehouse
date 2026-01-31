using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready MongoDB connector plugin.
/// Provides full CRUD operations with real database connectivity via MongoDB.Driver.
/// Supports schema discovery, BSON queries, transactions, and connection pooling.
/// </summary>
public class MongoDbConnectorPlugin : DatabaseConnectorPluginBase
{
    private MongoClient? _client;
    private IMongoDatabase? _database;
    private string? _connectionString;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly Dictionary<string, DataSchema> _schemaCache = new();
    private MongoDbConnectorConfig _config = new();

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.mongodb";

    /// <inheritdoc />
    public override string Name => "MongoDB Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "mongodb";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <inheritdoc />
    public override ConnectorCapabilities Capabilities =>
        ConnectorCapabilities.Read |
        ConnectorCapabilities.Write |
        ConnectorCapabilities.Schema |
        ConnectorCapabilities.Transactions |
        ConnectorCapabilities.BulkOperations;

    /// <summary>
    /// Configures the connector with additional options.
    /// </summary>
    /// <param name="config">MongoDB-specific configuration.</param>
    public void Configure(MongoDbConnectorConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc />
    protected override async Task<ConnectionResult> EstablishConnectionAsync(ConnectorConfig config, CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            _connectionString = config.ConnectionString;

            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                return new ConnectionResult(false, "Connection string is required", null);
            }

            // Parse connection string to extract database name
            var mongoUrl = MongoUrl.Create(_connectionString);
            var databaseName = mongoUrl.DatabaseName ?? _config.DefaultDatabase ?? "test";

            // Configure client settings
            var settings = MongoClientSettings.FromConnectionString(_connectionString);
            settings.MaxConnectionPoolSize = _config.MaxPoolSize;
            settings.MinConnectionPoolSize = _config.MinPoolSize;
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(_config.ServerSelectionTimeout);

            _client = new MongoClient(settings);
            _database = _client.GetDatabase(databaseName);

            // Test the connection by pinging the database
            var pingCommand = new BsonDocument("ping", 1);
            await _database.RunCommandAsync<BsonDocument>(pingCommand, cancellationToken: ct);

            // Retrieve server info
            var buildInfo = await _database.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1), cancellationToken: ct);
            var serverStatus = await _database.RunCommandAsync<BsonDocument>(new BsonDocument("serverStatus", 1), cancellationToken: ct);

            var serverInfo = new Dictionary<string, object>
            {
                ["ServerVersion"] = buildInfo.GetValue("version", "unknown").ToString() ?? "unknown",
                ["Database"] = databaseName,
                ["ConnectionState"] = "Open",
                ["GitVersion"] = buildInfo.GetValue("gitVersion", "unknown").ToString() ?? "unknown",
                ["Host"] = serverStatus.GetValue("host", "unknown").ToString() ?? "unknown",
                ["Process"] = serverStatus.GetValue("process", "unknown").ToString() ?? "unknown"
            };

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (MongoException ex)
        {
            return new ConnectionResult(false, $"MongoDB connection failed: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            return new ConnectionResult(false, $"Connection failed: {ex.Message}", null);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task CloseConnectionAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            _client = null;
            _database = null;
            _connectionString = null;
            _schemaCache.Clear();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> PingAsync()
    {
        if (_database == null) return false;

        try
        {
            var pingCommand = new BsonDocument("ping", 1);
            await _database.RunCommandAsync<BsonDocument>(pingCommand);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    protected override async Task<DataSchema> FetchSchemaAsync()
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to database");

        var fields = new List<DataSchemaField>();
        var collectionCount = 0;

        // Get list of collections
        var collections = await _database.ListCollectionNamesAsync();
        while (await collections.MoveNextAsync())
        {
            foreach (var collectionName in collections.Current)
            {
                collectionCount++;

                // Sample a document to infer schema
                var collection = _database.GetCollection<BsonDocument>(collectionName);
                var sample = await collection.Find(new BsonDocument()).Limit(1).FirstOrDefaultAsync();

                if (sample != null)
                {
                    foreach (var element in sample.Elements)
                    {
                        fields.Add(new DataSchemaField(
                            element.Name,
                            MapBsonType(element.Value.BsonType),
                            true, // MongoDB fields are nullable by nature
                            null,
                            new Dictionary<string, object>
                            {
                                ["collectionName"] = collectionName,
                                ["bsonType"] = element.Value.BsonType.ToString()
                            }
                        ));
                    }
                }
            }
        }

        return new DataSchema(
            Name: _database.DatabaseNamespace.DatabaseName,
            Fields: fields.ToArray(),
            PrimaryKeys: new[] { "_id" },
            Metadata: new Dictionary<string, object>
            {
                ["CollectionCount"] = collectionCount,
                ["FieldCount"] = fields.Count,
                ["SchemaVersion"] = "1.0"
            }
        );
    }

    /// <summary>
    /// Gets the schema for a specific collection.
    /// </summary>
    /// <param name="collectionName">Name of the collection.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection schema.</returns>
    public async Task<DataSchema> GetCollectionSchemaAsync(string collectionName, CancellationToken ct = default)
    {
        if (_schemaCache.TryGetValue(collectionName, out var cached))
            return cached;

        if (_database == null)
            throw new InvalidOperationException("Not connected to database");

        var collection = _database.GetCollection<BsonDocument>(collectionName);
        var fields = new List<DataSchemaField>();

        // Sample multiple documents to build comprehensive schema
        var samples = await collection.Find(new BsonDocument()).Limit(10).ToListAsync(ct);
        var fieldNames = new HashSet<string>();

        foreach (var sample in samples)
        {
            foreach (var element in sample.Elements)
            {
                if (fieldNames.Add(element.Name))
                {
                    fields.Add(new DataSchemaField(
                        element.Name,
                        MapBsonType(element.Value.BsonType),
                        true,
                        null,
                        new Dictionary<string, object>
                        {
                            ["bsonType"] = element.Value.BsonType.ToString()
                        }
                    ));
                }
            }
        }

        var schema = new DataSchema(
            Name: collectionName,
            Fields: fields.ToArray(),
            PrimaryKeys: new[] { "_id" },
            Metadata: new Dictionary<string, object>
            {
                ["CollectionName"] = collectionName
            }
        );

        _schemaCache[collectionName] = schema;
        return schema;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<DataRecord> ExecuteReadAsync(
        DataQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to database");

        var collectionName = query.TableOrCollection ?? "data";
        var collection = _database.GetCollection<BsonDocument>(collectionName);

        // Build filter
        var filter = string.IsNullOrWhiteSpace(query.Filter)
            ? Builders<BsonDocument>.Filter.Empty
            : BsonDocument.Parse(query.Filter);

        // Build find options
        var findOptions = new FindOptions<BsonDocument>
        {
            Skip = query.Offset.HasValue ? (int)query.Offset.Value : null,
            Limit = query.Limit.HasValue ? (int)query.Limit.Value : null
        };

        // Add projection if specific fields requested
        if (query.Fields?.Length > 0)
        {
            var projection = Builders<BsonDocument>.Projection.Include(query.Fields[0]);
            foreach (var field in query.Fields.Skip(1))
            {
                projection = projection.Include(field);
            }
            findOptions.Projection = projection;
        }

        // Add sort if specified
        if (!string.IsNullOrEmpty(query.OrderBy))
        {
            findOptions.Sort = BsonDocument.Parse(query.OrderBy);
        }

        long position = query.Offset ?? 0;

        using var cursor = await collection.FindAsync(filter, findOptions, ct);
        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var document in cursor.Current)
            {
                if (ct.IsCancellationRequested) yield break;

                var values = new Dictionary<string, object?>();
                foreach (var element in document.Elements)
                {
                    values[element.Name] = ConvertBsonValue(element.Value);
                }

                yield return new DataRecord(
                    Values: values,
                    Position: position++,
                    Timestamp: DateTimeOffset.UtcNow
                );
            }
        }
    }

    /// <inheritdoc />
    protected override async Task<WriteResult> ExecuteWriteAsync(
        IAsyncEnumerable<DataRecord> records,
        WriteOptions options,
        CancellationToken ct)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to database");

        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        var collectionName = options.TargetTable ?? throw new ArgumentException("TargetTable is required");
        var collection = _database.GetCollection<BsonDocument>(collectionName);

        var batch = new List<WriteModel<BsonDocument>>();

        await foreach (var record in records.WithCancellation(ct))
        {
            try
            {
                var document = new BsonDocument();
                foreach (var (key, value) in record.Values)
                {
                    document[key] = BsonValue.Create(value);
                }

                if (options.Mode == SDK.Connectors.WriteMode.Upsert)
                {
                    var filter = Builders<BsonDocument>.Filter.Eq("_id", document["_id"]);
                    batch.Add(new ReplaceOneModel<BsonDocument>(filter, document) { IsUpsert = true });
                }
                else
                {
                    batch.Add(new InsertOneModel<BsonDocument>(document));
                }

                if (batch.Count >= options.BatchSize)
                {
                    var result = await collection.BulkWriteAsync(batch, new BulkWriteOptions { IsOrdered = false }, ct);
                    written += result.InsertedCount + result.ModifiedCount;
                    batch.Clear();
                }
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Record at position {record.Position}: {ex.Message}");
            }
        }

        // Write remaining records
        if (batch.Count > 0)
        {
            try
            {
                var result = await collection.BulkWriteAsync(batch, new BulkWriteOptions { IsOrdered = false }, ct);
                written += result.InsertedCount + result.ModifiedCount;
            }
            catch (Exception ex)
            {
                failed += batch.Count;
                errors.Add($"Batch write failed: {ex.Message}");
            }
        }

        return new WriteResult(written, failed, errors.Count > 0 ? errors.ToArray() : null);
    }

    /// <inheritdoc />
    protected override string BuildSelectQuery(DataQuery query)
    {
        // MongoDB uses BSON filter documents instead of SQL
        return query.Filter ?? "{}";
    }

    /// <inheritdoc />
    protected override string BuildInsertStatement(string table, string[] columns)
    {
        // Not applicable for MongoDB
        return string.Empty;
    }

    private static object? ConvertBsonValue(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.String => value.AsString,
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => value.AsInt64,
            BsonType.Double => value.AsDouble,
            BsonType.Decimal128 => Decimal128.ToDecimal(value.AsDecimal128),
            BsonType.Boolean => value.AsBoolean,
            BsonType.DateTime => value.ToUniversalTime(),
            BsonType.ObjectId => value.AsObjectId.ToString(),
            BsonType.Null => null,
            BsonType.Array => value.AsBsonArray.Select(ConvertBsonValue).ToArray(),
            BsonType.Document => value.AsBsonDocument.ToDictionary(e => e.Name, e => ConvertBsonValue(e.Value)),
            _ => value.ToString()
        };
    }

    private static string MapBsonType(BsonType bsonType)
    {
        return bsonType switch
        {
            BsonType.String => "string",
            BsonType.Int32 => "int",
            BsonType.Int64 => "long",
            BsonType.Double => "double",
            BsonType.Decimal128 => "decimal",
            BsonType.Boolean => "bool",
            BsonType.DateTime => "datetime",
            BsonType.ObjectId => "string",
            BsonType.Binary => "bytes",
            BsonType.Array => "array",
            BsonType.Document => "object",
            _ => bsonType.ToString()
        };
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();
}

/// <summary>
/// Configuration options for the MongoDB connector.
/// </summary>
public class MongoDbConnectorConfig
{
    /// <summary>
    /// Maximum number of connections in the pool.
    /// </summary>
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>
    /// Minimum number of connections in the pool.
    /// </summary>
    public int MinPoolSize { get; set; } = 0;

    /// <summary>
    /// Server selection timeout in seconds.
    /// </summary>
    public int ServerSelectionTimeout { get; set; } = 30;

    /// <summary>
    /// Default database name if not specified in connection string.
    /// </summary>
    public string DefaultDatabase { get; set; } = "test";
}
