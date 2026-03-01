using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using Microsoft.Azure.Cosmos;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.NoSQL;

/// <summary>
/// Azure Cosmos DB (DocumentDB API) storage strategy with production-ready features:
/// - Globally distributed database
/// - Multi-model support
/// - Automatic indexing
/// - Tunable consistency levels
/// - Automatic scaling
/// - SLA-backed availability
/// - Multi-region writes
/// </summary>
public sealed class DocumentDbStorageStrategy : DatabaseStorageStrategyBase
{
    private CosmosClient? _client;
    private Database? _database;
    private Container? _container;
    private string _databaseName = "datawarehouse";
    private string _containerName = "storage";
    private string _partitionKeyPath = "/partitionKey";
    private string _defaultPartitionKey = "default";
    private ConsistencyLevel _consistencyLevel = ConsistencyLevel.Session;

    public override string StrategyId => "documentdb";
    public override string Name => "Azure Cosmos DB (DocumentDB) Storage";
    public override StorageTier Tier => StorageTier.Hot;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.NoSQL;
    public override string Engine => "CosmosDB";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = false,
        SupportsVersioning = true, // _etag
        SupportsTiering = true, // TTL, analytical store
        SupportsEncryption = true,
        SupportsCompression = false,
        SupportsMultipart = false,
        MaxObjectSize = 2L * 1024 * 1024, // 2MB document limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Eventual // Tunable
    };

    public override bool SupportsTransactions => true;
    public override bool SupportsSql => true; // SQL-like query language

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _databaseName = GetConfiguration("DatabaseName", "datawarehouse");
        _containerName = GetConfiguration("ContainerName", "storage");
        _partitionKeyPath = GetConfiguration("PartitionKeyPath", "/partitionKey");
        _defaultPartitionKey = GetConfiguration("DefaultPartitionKey", "default");
        _consistencyLevel = Enum.Parse<ConsistencyLevel>(GetConfiguration("ConsistencyLevel", "Session"));

        var connectionString = GetConnectionString();
        var endpoint = GetConfiguration<string?>("Endpoint", null);
        var key = GetConfiguration<string?>("AccountKey", null);

        if (!string.IsNullOrEmpty(connectionString))
        {
            _client = new CosmosClient(connectionString, new CosmosClientOptions
            {
                ConsistencyLevel = _consistencyLevel,
                ApplicationName = "DataWarehouse"
            });
        }
        else if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(key))
        {
            _client = new CosmosClient(endpoint, key, new CosmosClientOptions
            {
                ConsistencyLevel = _consistencyLevel,
                ApplicationName = "DataWarehouse"
            });
        }
        else
        {
            throw new InvalidOperationException("Either ConnectionString or Endpoint+AccountKey is required");
        }

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        _database = _client!.GetDatabase(_databaseName);
        _container = _database.GetContainer(_containerName);

        // Test connection
        await _container.ReadContainerAsync(cancellationToken: ct);
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        _client?.Dispose();
        _client = null;
        _database = null;
        _container = null;
        await Task.CompletedTask;
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        _database = await _client!.CreateDatabaseIfNotExistsAsync(_databaseName, cancellationToken: ct);

        var containerProperties = new ContainerProperties(_containerName, _partitionKeyPath)
        {
            IndexingPolicy = new IndexingPolicy
            {
                Automatic = true,
                IndexingMode = IndexingMode.Consistent,
                IncludedPaths = { new IncludedPath { Path = "/*" } },
                ExcludedPaths = { new ExcludedPath { Path = "/data/*" } }
            }
        };

        _container = await _database.CreateContainerIfNotExistsAsync(containerProperties, cancellationToken: ct);
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var partitionKey = GetPartitionKey(key);

        // Preserve CreatedAt on update: read the existing document's creation timestamp.
        DateTime createdAt = now;
        try
        {
            var existingResponse = await _container!.ReadItemAsync<StorageDocument>(
                key, new PartitionKey(partitionKey), cancellationToken: ct);
            createdAt = existingResponse.Resource.CreatedAt;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // New document — use current time as CreatedAt.
        }

        var document = new StorageDocument
        {
            Id = key,
            PartitionKey = partitionKey,
            Data = Convert.ToBase64String(data),
            Size = data.LongLength,
            ContentType = contentType,
            ETag = etag,
            Metadata = metadata?.ToDictionary(k => k.Key, v => v.Value),
            CreatedAt = createdAt,
            ModifiedAt = now
        };

        var response = await _container!.UpsertItemAsync(
            document,
            new PartitionKey(partitionKey),
            new ItemRequestOptions { EnableContentResponseOnWrite = false },
            ct);

        return new StorageObjectMetadata
        {
            Key = key,
            Size = data.LongLength,
            Created = createdAt,
            Modified = now,
            ETag = etag,
            ContentType = contentType,
            CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
            VersionId = response.ETag,
            Tier = Tier
        };
    }

    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct)
    {
        var partitionKey = GetPartitionKey(key);

        try
        {
            var response = await _container!.ReadItemAsync<StorageDocument>(
                key,
                new PartitionKey(partitionKey),
                cancellationToken: ct);

            return Convert.FromBase64String(response.Resource.Data);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        var partitionKey = GetPartitionKey(key);

        try
        {
            await _container!.DeleteItemAsync<StorageDocument>(
                key,
                new PartitionKey(partitionKey),
                cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already deleted
        }

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var partitionKey = GetPartitionKey(key);

        try
        {
            await _container!.ReadItemAsync<StorageDocument>(
                key,
                new PartitionKey(partitionKey),
                requestOptions: null,
                ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        QueryDefinition query;
        if (string.IsNullOrEmpty(prefix))
        {
            query = new QueryDefinition("SELECT c.id, c.size, c.contentType, c.eTag, c.metadata, c.createdAt, c.modifiedAt FROM c");
        }
        else
        {
            query = new QueryDefinition("SELECT c.id, c.size, c.contentType, c.eTag, c.metadata, c.createdAt, c.modifiedAt FROM c WHERE STARTSWITH(c.id, @prefix)")
                .WithParameter("@prefix", prefix);
        }

        using var iterator = _container!.GetItemQueryIterator<StorageDocument>(
            query,
            requestOptions: new QueryRequestOptions { MaxItemCount = 100 });

        while (iterator.HasMoreResults)
        {
            ct.ThrowIfCancellationRequested();

            var response = await iterator.ReadNextAsync(ct);

            foreach (var doc in response)
            {
                yield return new StorageObjectMetadata
                {
                    Key = doc.Id,
                    Size = doc.Size,
                    ContentType = doc.ContentType,
                    ETag = doc.ETag,
                    CustomMetadata = doc.Metadata as IReadOnlyDictionary<string, string>,
                    Created = doc.CreatedAt,
                    Modified = doc.ModifiedAt,
                    Tier = Tier
                };
            }
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        var partitionKey = GetPartitionKey(key);

        try
        {
            var response = await _container!.ReadItemAsync<StorageDocument>(
                key,
                new PartitionKey(partitionKey),
                cancellationToken: ct);

            var doc = response.Resource;
            return new StorageObjectMetadata
            {
                Key = doc.Id,
                Size = doc.Size,
                ContentType = doc.ContentType,
                ETag = doc.ETag,
                CustomMetadata = doc.Metadata as IReadOnlyDictionary<string, string>,
                Created = doc.CreatedAt,
                Modified = doc.ModifiedAt,
                VersionId = response.ETag,
                Tier = Tier
            };
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            await _container!.ReadContainerAsync(cancellationToken: ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct)
    {
        return new CosmosTransaction(_container!);
    }

    private string GetPartitionKey(string key)
    {
        // Use first path segment as partition key, or the configurable default if no path
        var slashIndex = key.IndexOf('/');
        return slashIndex > 0 ? key.Substring(0, slashIndex) : _defaultPartitionKey;
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _client?.Dispose();
        await base.DisposeAsyncCore();
    }

    private sealed class StorageDocument
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("partitionKey")]
        public string PartitionKey { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("data")]
        public string Data { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("size")]
        public long Size { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("contentType")]
        public string? ContentType { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("eTag")]
        public string? ETag { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("metadata")]
        public Dictionary<string, string>? Metadata { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("modifiedAt")]
        public DateTime ModifiedAt { get; set; }
    }

    private sealed class CosmosTransaction : IDatabaseTransaction
    {
        private readonly Container _container;
        private readonly TransactionalBatch _batch;
        private bool _disposed;
        private bool _committed;

        public string TransactionId { get; } = Guid.NewGuid().ToString("N");
        public System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.Serializable;

        public CosmosTransaction(Container container)
        {
            _container = container;
            _batch = container.CreateTransactionalBatch(new PartitionKey("default"));
        }

        public async Task CommitAsync(CancellationToken ct = default)
        {
            if (_committed) return;
            await _batch.ExecuteAsync(ct);
            _committed = true;
        }

        public Task RollbackAsync(CancellationToken ct = default)
        {
            // P2-2841: Cosmos TransactionalBatch is rolled back simply by not executing it.
            // Guard against rollback after commit to surface contract violations to the caller.
            if (_committed)
                throw new InvalidOperationException(
                    "Cannot roll back a Cosmos transaction that has already been committed.");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
