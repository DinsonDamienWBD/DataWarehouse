using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using Neo4j.Driver;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Graph;

/// <summary>
/// Neo4j graph database storage strategy with production-ready features:
/// - Native graph database with Cypher query language
/// - ACID transactions
/// - Node and relationship storage
/// - Property graphs
/// - Graph algorithms support
/// - Clustering for high availability
/// - Causal consistency
/// </summary>
public sealed class Neo4jStorageStrategy : DatabaseStorageStrategyBase
{
    private IDriver? _driver;
    private string _nodeLabel = "StorageObject";
    private bool _enableEncryption = true;

    public override string StrategyId => "neo4j";
    public override string Name => "Neo4j Graph Storage";
    public override StorageTier Tier => StorageTier.Warm;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.Graph;
    public override string Engine => "Neo4j";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = false,
        SupportsVersioning = false,
        SupportsTiering = false,
        SupportsEncryption = true,
        SupportsCompression = false,
        SupportsMultipart = false,
        MaxObjectSize = 10L * 1024 * 1024, // 10MB practical limit for properties
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Strong
    };

    public override bool SupportsTransactions => true;
    public override bool SupportsSql => false;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _nodeLabel = GetConfiguration("NodeLabel", "StorageObject");
        _enableEncryption = GetConfiguration("EnableEncryption", true);

        var connectionString = GetConnectionString();
        var username = GetConfiguration<string?>("Username", null);
        var password = GetConfiguration<string?>("Password", null);

        var authToken = string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)
            ? AuthTokens.None
            : AuthTokens.Basic(username, password);

        var builder = GraphDatabase.Driver(connectionString, authToken, o =>
        {
            o.WithMaxConnectionPoolSize(GetConfiguration("MaxPoolSize", 100));
            o.WithConnectionTimeout(TimeSpan.FromSeconds(GetConfiguration("ConnectionTimeoutSeconds", 30)));
            if (_enableEncryption)
            {
                o.WithEncryptionLevel(EncryptionLevel.Encrypted);
            }
        });

        _driver = builder;

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        await _driver!.VerifyConnectivityAsync();
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        if (_driver != null)
        {
            await _driver.DisposeAsync();
            _driver = null;
        }
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        await using var session = _driver!.AsyncSession();

        // Create uniqueness constraint on key
        await session.ExecuteWriteAsync(async tx =>
        {
            try
            {
                await tx.RunAsync($@"
                    CREATE CONSTRAINT storage_key_unique IF NOT EXISTS
                    FOR (n:{_nodeLabel})
                    REQUIRE n.key IS UNIQUE");
            }
            catch
            {
                // Constraint might already exist
            }
        });

        // Create index on key
        await session.ExecuteWriteAsync(async tx =>
        {
            try
            {
                await tx.RunAsync($@"
                    CREATE INDEX storage_key_index IF NOT EXISTS
                    FOR (n:{_nodeLabel})
                    ON (n.key)");
            }
            catch
            {
                // Index might already exist
            }
        });
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var dataBase64 = Convert.ToBase64String(data);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : null;

        await using var session = _driver!.AsyncSession();

        var result = await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync($@"
                MERGE (n:{_nodeLabel} {{key: $key}})
                ON CREATE SET
                    n.data = $data,
                    n.size = $size,
                    n.contentType = $contentType,
                    n.etag = $etag,
                    n.metadata = $metadata,
                    n.createdAt = $createdAt,
                    n.modifiedAt = $modifiedAt
                ON MATCH SET
                    n.data = $data,
                    n.size = $size,
                    n.contentType = $contentType,
                    n.etag = $etag,
                    n.metadata = $metadata,
                    n.modifiedAt = $modifiedAt
                RETURN n.createdAt AS createdAt, n.modifiedAt AS modifiedAt",
                new
                {
                    key,
                    data = dataBase64,
                    size = data.LongLength,
                    contentType = contentType ?? "",
                    etag,
                    metadata = metadataJson ?? "",
                    createdAt = now.ToString("o"),
                    modifiedAt = now.ToString("o")
                });

            var record = await cursor.SingleAsync();
            return (
                createdAt: DateTime.Parse(record["createdAt"].As<string>()),
                modifiedAt: DateTime.Parse(record["modifiedAt"].As<string>())
            );
        });

        return new StorageObjectMetadata
        {
            Key = key,
            Size = data.LongLength,
            Created = result.createdAt,
            Modified = result.modifiedAt,
            ETag = etag,
            ContentType = contentType,
            CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
            Tier = Tier
        };
    }

    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct)
    {
        await using var session = _driver!.AsyncSession();

        var result = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync($@"
                MATCH (n:{_nodeLabel} {{key: $key}})
                RETURN n.data AS data",
                new { key });

            var records = await cursor.ToListAsync();
            return records.FirstOrDefault();
        });

        if (result == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var dataBase64 = result["data"].As<string>();
        return Convert.FromBase64String(dataBase64);
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        await using var session = _driver!.AsyncSession();

        var size = await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync($@"
                MATCH (n:{_nodeLabel} {{key: $key}})
                WITH n, n.size AS size
                DELETE n
                RETURN size",
                new { key });

            var records = await cursor.ToListAsync();
            return records.FirstOrDefault()?["size"].As<long?>() ?? 0L;
        });

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        await using var session = _driver!.AsyncSession();

        var exists = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync($@"
                MATCH (n:{_nodeLabel} {{key: $key}})
                RETURN COUNT(n) > 0 AS exists",
                new { key });

            var record = await cursor.SingleAsync();
            return record["exists"].As<bool>();
        });

        return exists;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var session = _driver!.AsyncSession();

        var query = string.IsNullOrEmpty(prefix)
            ? $"MATCH (n:{_nodeLabel}) RETURN n ORDER BY n.key"
            : $"MATCH (n:{_nodeLabel}) WHERE n.key STARTS WITH $prefix RETURN n ORDER BY n.key";

        var cursor = await session.RunAsync(query, new { prefix });

        while (await cursor.FetchAsync())
        {
            ct.ThrowIfCancellationRequested();

            var node = cursor.Current["n"].As<INode>();

            Dictionary<string, string>? customMetadata = null;
            if (node.Properties.TryGetValue("metadata", out var metaObj) && metaObj is string metaJson && !string.IsNullOrEmpty(metaJson))
            {
                customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metaJson, JsonOptions);
            }

            yield return new StorageObjectMetadata
            {
                Key = node.Properties["key"].As<string>(),
                Size = node.Properties["size"].As<long>(),
                ContentType = node.Properties.TryGetValue("contentType", out var ct2) ? ct2.As<string>() : null,
                ETag = node.Properties.TryGetValue("etag", out var etag) ? etag.As<string>() : null,
                CustomMetadata = customMetadata,
                Created = DateTime.Parse(node.Properties["createdAt"].As<string>()),
                Modified = DateTime.Parse(node.Properties["modifiedAt"].As<string>()),
                Tier = Tier
            };
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        await using var session = _driver!.AsyncSession();

        var result = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync($@"
                MATCH (n:{_nodeLabel} {{key: $key}})
                RETURN n.size AS size, n.contentType AS contentType, n.etag AS etag,
                       n.metadata AS metadata, n.createdAt AS createdAt, n.modifiedAt AS modifiedAt",
                new { key });

            return await cursor.SingleAsync();
        });

        if (result == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        Dictionary<string, string>? customMetadata = null;
        var metaJson = result["metadata"].As<string>();
        if (!string.IsNullOrEmpty(metaJson))
        {
            customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metaJson, JsonOptions);
        }

        return new StorageObjectMetadata
        {
            Key = key,
            Size = result["size"].As<long>(),
            ContentType = result["contentType"].As<string>(),
            ETag = result["etag"].As<string>(),
            CustomMetadata = customMetadata,
            Created = DateTime.Parse(result["createdAt"].As<string>()),
            Modified = DateTime.Parse(result["modifiedAt"].As<string>()),
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            await _driver!.VerifyConnectivityAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct)
    {
        var session = _driver!.AsyncSession();
        var tx = await session.BeginTransactionAsync();
        return new Neo4jTransaction(session, tx);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (_driver != null)
        {
            await _driver.DisposeAsync();
        }
        await base.DisposeAsyncCore();
    }

    private sealed class Neo4jTransaction : IDatabaseTransaction
    {
        private readonly IAsyncSession _session;
        private readonly IAsyncTransaction _transaction;
        private bool _disposed;

        public string TransactionId { get; } = Guid.NewGuid().ToString("N");
        public System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.ReadCommitted;

        public Neo4jTransaction(IAsyncSession session, IAsyncTransaction transaction)
        {
            _session = session;
            _transaction = transaction;
        }

        public async Task CommitAsync(CancellationToken ct = default)
        {
            await _transaction.CommitAsync();
        }

        public async Task RollbackAsync(CancellationToken ct = default)
        {
            await _transaction.RollbackAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            await _session.DisposeAsync();
        }
    }
}
