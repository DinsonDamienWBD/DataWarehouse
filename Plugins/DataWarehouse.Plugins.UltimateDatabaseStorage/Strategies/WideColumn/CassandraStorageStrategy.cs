using Cassandra;
using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.WideColumn;

/// <summary>
/// Apache Cassandra wide-column storage strategy with production-ready features:
/// - Distributed NoSQL database
/// - Linear scalability
/// - Tunable consistency
/// - Partition-based data model
/// - CQL query language
/// - Multi-datacenter replication
/// - High write throughput
/// </summary>
public sealed class CassandraStorageStrategy : DatabaseStorageStrategyBase
{
    private Cluster? _cluster;
    private ISession? _session;
    private string _keyspace = "datawarehouse";
    private string _tableName = "storage";
    private ConsistencyLevel _readConsistency = ConsistencyLevel.LocalQuorum;
    private ConsistencyLevel _writeConsistency = ConsistencyLevel.LocalQuorum;
    private PreparedStatement? _insertStmt;
    private PreparedStatement? _selectStmt;
    private PreparedStatement? _deleteStmt;
    private PreparedStatement? _existsStmt;

    public override string StrategyId => "cassandra";
    public override string Name => "Cassandra Wide-Column Storage";
    public override StorageTier Tier => StorageTier.Warm;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.WideColumn;
    public override string Engine => "Cassandra";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = false,
        SupportsVersioning = false,
        SupportsTiering = true,
        SupportsEncryption = true,
        SupportsCompression = true,
        SupportsMultipart = false,
        MaxObjectSize = 256L * 1024 * 1024, // 256MB practical limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Eventual
    };

    public override bool SupportsTransactions => false;
    public override bool SupportsSql => false;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _keyspace = GetConfiguration("Keyspace", "datawarehouse");
        _tableName = GetConfiguration("TableName", "storage");
        _readConsistency = Enum.Parse<ConsistencyLevel>(GetConfiguration("ReadConsistency", "LocalQuorum"));
        _writeConsistency = Enum.Parse<ConsistencyLevel>(GetConfiguration("WriteConsistency", "LocalQuorum"));

        var connectionString = GetConnectionString();
        var contactPoints = connectionString.Split(',', StringSplitOptions.RemoveEmptyEntries);

        var builder = Cluster.Builder();
        foreach (var contactPoint in contactPoints)
        {
            builder.AddContactPoint(contactPoint.Trim());
        }

        var username = GetConfiguration<string?>("Username", null);
        var password = GetConfiguration<string?>("Password", null);
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            builder.WithCredentials(username, password);
        }

        builder.WithQueryOptions(new QueryOptions()
            .SetConsistencyLevel(_readConsistency));

        _cluster = builder.Build();
        _session = await _cluster.ConnectAsync();

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        // Session is already connected during initialization
        await Task.CompletedTask;
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        _session?.Dispose();
        _cluster?.Dispose();
        _session = null;
        _cluster = null;
        await Task.CompletedTask;
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        // Create keyspace if not exists
        await _session!.ExecuteAsync(new SimpleStatement($@"
            CREATE KEYSPACE IF NOT EXISTS {_keyspace}
            WITH replication = {{'class': 'SimpleStrategy', 'replication_factor': 3}}
            AND durable_writes = true"));

        await _session.ExecuteAsync(new SimpleStatement($"USE {_keyspace}"));

        // Create table
        await _session.ExecuteAsync(new SimpleStatement($@"
            CREATE TABLE IF NOT EXISTS {_tableName} (
                key text PRIMARY KEY,
                data blob,
                size bigint,
                content_type text,
                etag text,
                metadata text,
                created_at timestamp,
                modified_at timestamp
            )"));

        // Prepare statements
        _insertStmt = await _session.PrepareAsync($@"
            INSERT INTO {_tableName} (key, data, size, content_type, etag, metadata, created_at, modified_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)");

        _selectStmt = await _session.PrepareAsync($@"
            SELECT data, size, content_type, etag, metadata, created_at, modified_at
            FROM {_tableName} WHERE key = ?");

        _deleteStmt = await _session.PrepareAsync($"DELETE FROM {_tableName} WHERE key = ?");

        _existsStmt = await _session.PrepareAsync($"SELECT key FROM {_tableName} WHERE key = ?");
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : null;

        var bound = _insertStmt!.Bind(key, data, data.LongLength, contentType, etag, metadataJson, now, now)
            .SetConsistencyLevel(_writeConsistency);

        await _session!.ExecuteAsync(bound);

        return new StorageObjectMetadata
        {
            Key = key,
            Size = data.LongLength,
            Created = now,
            Modified = now,
            ETag = etag,
            ContentType = contentType,
            CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
            Tier = Tier
        };
    }

    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct)
    {
        var bound = _selectStmt!.Bind(key).SetConsistencyLevel(_readConsistency);
        var result = await _session!.ExecuteAsync(bound);
        var row = result.FirstOrDefault();

        if (row == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return row.GetValue<byte[]>("data");
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        // Get size before deletion
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        var bound = _deleteStmt!.Bind(key).SetConsistencyLevel(_writeConsistency);
        await _session!.ExecuteAsync(bound);

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var bound = _existsStmt!.Bind(key).SetConsistencyLevel(_readConsistency);
        var result = await _session!.ExecuteAsync(bound);
        return result.Any();
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        // Note: Full table scan - consider using secondary index for prefix queries
        SimpleStatement statement;
        if (string.IsNullOrEmpty(prefix))
        {
            statement = new SimpleStatement($"SELECT key, size, content_type, etag, metadata, created_at, modified_at FROM {_tableName}");
        }
        else
        {
            statement = new SimpleStatement($"SELECT key, size, content_type, etag, metadata, created_at, modified_at FROM {_tableName} WHERE key >= ? ALLOW FILTERING", prefix);
        }

        var result = await _session!.ExecuteAsync(statement.SetConsistencyLevel(_readConsistency));

        foreach (var row in result)
        {
            ct.ThrowIfCancellationRequested();

            var key = row.GetValue<string>("key");
            if (!string.IsNullOrEmpty(prefix) && !key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, string>? customMetadata = null;
            var metadataJson = row.GetValue<string>("metadata");
            if (!string.IsNullOrEmpty(metadataJson))
            {
                customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
            }

            yield return new StorageObjectMetadata
            {
                Key = key,
                Size = row.GetValue<long>("size"),
                ContentType = row.GetValue<string>("content_type"),
                ETag = row.GetValue<string>("etag"),
                CustomMetadata = customMetadata,
                Created = row.GetValue<DateTimeOffset>("created_at").DateTime,
                Modified = row.GetValue<DateTimeOffset>("modified_at").DateTime,
                Tier = Tier
            };
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        var bound = _selectStmt!.Bind(key).SetConsistencyLevel(_readConsistency);
        var result = await _session!.ExecuteAsync(bound);
        var row = result.FirstOrDefault();

        if (row == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        Dictionary<string, string>? customMetadata = null;
        var metadataJson = row.GetValue<string>("metadata");
        if (!string.IsNullOrEmpty(metadataJson))
        {
            customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
        }

        return new StorageObjectMetadata
        {
            Key = key,
            Size = row.GetValue<long>("size"),
            ContentType = row.GetValue<string>("content_type"),
            ETag = row.GetValue<string>("etag"),
            CustomMetadata = customMetadata,
            Created = row.GetValue<DateTimeOffset>("created_at").DateTime,
            Modified = row.GetValue<DateTimeOffset>("modified_at").DateTime,
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            var result = await _session!.ExecuteAsync(new SimpleStatement("SELECT now() FROM system.local"));
            return result.Any();
        }
        catch
        {
            return false;
        }
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _session?.Dispose();
        _cluster?.Dispose();
        await base.DisposeAsyncCore();
    }
}
