using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using Google.Cloud.Spanner.Data;
using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.CloudNative;

/// <summary>
/// Google Cloud Spanner storage strategy with production-ready features:
/// - Globally distributed relational database
/// - Strong external consistency
/// - Horizontal scaling with automatic sharding
/// - 99.999% availability SLA
/// - ACID transactions across continents
/// - SQL support with ANSI 2011 compliance
/// - Automatic replication and failover
/// - Integration with Google Cloud services
/// </summary>
public sealed class SpannerStorageStrategy : DatabaseStorageStrategyBase
{
    private SpannerConnection? _connection;
    private string _connectionString = string.Empty;
    private string _projectId = string.Empty;
    private string _instanceId = string.Empty;
    private string _databaseId = string.Empty;
    private string _tableName = "DataWarehouseStorage";

    public override string StrategyId => "spanner";
    public override string Name => "Google Cloud Spanner Storage";
    public override StorageTier Tier => StorageTier.Warm;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.NewSQL;
    public override string Engine => "Cloud Spanner";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = true,
        SupportsVersioning = true, // Via commit timestamps
        SupportsTiering = false,
        SupportsEncryption = true,
        SupportsCompression = false,
        SupportsMultipart = false,
        MaxObjectSize = 10L * 1024 * 1024, // 10MB BYTES column limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Strong
    };

    public override bool SupportsTransactions => true;
    public override bool SupportsSql => true;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _projectId = GetConfiguration<string>("ProjectId")
            ?? throw new InvalidOperationException("Spanner ProjectId is required");
        _instanceId = GetConfiguration<string>("InstanceId")
            ?? throw new InvalidOperationException("Spanner InstanceId is required");
        _databaseId = GetConfiguration<string>("DatabaseId")
            ?? throw new InvalidOperationException("Spanner DatabaseId is required");
        _tableName = GetConfiguration("TableName", "DataWarehouseStorage");

        _connectionString = $"Data Source=projects/{_projectId}/instances/{_instanceId}/databases/{_databaseId}";

        await Task.CompletedTask;
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        _connection = new SpannerConnection(_connectionString);
        await _connection.OpenAsync(ct);

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        if (_connection != null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        ValidateSqlIdentifier(_tableName, nameof(_tableName));

        // Check if table exists
        var checkCmd = _connection!.CreateSelectCommand(
            @"SELECT COUNT(*) FROM information_schema.tables
              WHERE table_catalog = '' AND table_schema = '' AND table_name = @tableName");
        checkCmd.Parameters.Add("tableName", SpannerDbType.String, _tableName);

        var count = (long)(await checkCmd.ExecuteScalarAsync(ct) ?? 0);
        if (count > 0) return;

        // Create table using DDL via the Spanner DDL execution API
        var ddl = $@"
            CREATE TABLE {_tableName} (
                StorageKey STRING(1024) NOT NULL,
                Data BYTES(MAX) NOT NULL,
                Size INT64 NOT NULL,
                ContentType STRING(255),
                ETag STRING(64) NOT NULL,
                Metadata JSON,
                CreatedAt TIMESTAMP NOT NULL OPTIONS (allow_commit_timestamp=true),
                ModifiedAt TIMESTAMP NOT NULL OPTIONS (allow_commit_timestamp=true)
            ) PRIMARY KEY (StorageKey)";

        // Execute DDL using SpannerConnection's CreateDdlCommand
        using var ddlCmd = _connection.CreateDdlCommand(ddl);
        await ddlCmd.ExecuteNonQueryAsync(ct);
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : null;

        using var cmd = _connection!.CreateInsertOrUpdateCommand(_tableName, new SpannerParameterCollection
        {
            { "StorageKey", SpannerDbType.String, key },
            { "Data", SpannerDbType.Bytes, data },
            { "Size", SpannerDbType.Int64, data.LongLength },
            { "ContentType", SpannerDbType.String, contentType },
            { "ETag", SpannerDbType.String, etag },
            { "Metadata", SpannerDbType.Json, metadataJson },
            { "CreatedAt", SpannerDbType.Timestamp, SpannerParameter.CommitTimestamp },
            { "ModifiedAt", SpannerDbType.Timestamp, SpannerParameter.CommitTimestamp }
        });

        await cmd.ExecuteNonQueryAsync(ct);

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
        using var cmd = _connection!.CreateSelectCommand(
            $"SELECT Data FROM {_tableName} WHERE StorageKey = @key");
        cmd.Parameters.Add("key", SpannerDbType.String, key);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return reader.GetFieldValue<byte[]>(0);
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        // Get size first
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        using var cmd = _connection!.CreateDeleteCommand(_tableName, new SpannerParameterCollection
        {
            { "StorageKey", SpannerDbType.String, key }
        });

        await cmd.ExecuteNonQueryAsync(ct);

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        using var cmd = _connection!.CreateSelectCommand(
            $"SELECT 1 FROM {_tableName} WHERE StorageKey = @key LIMIT 1");
        cmd.Parameters.Add("key", SpannerDbType.String, key);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result != null;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var sql = string.IsNullOrEmpty(prefix)
            ? $"SELECT StorageKey, Size, ContentType, ETag, Metadata, CreatedAt, ModifiedAt FROM {_tableName} ORDER BY StorageKey"
            : $"SELECT StorageKey, Size, ContentType, ETag, Metadata, CreatedAt, ModifiedAt FROM {_tableName} WHERE STARTS_WITH(StorageKey, @prefix) ORDER BY StorageKey";

        using var cmd = _connection!.CreateSelectCommand(sql);

        if (!string.IsNullOrEmpty(prefix))
        {
            cmd.Parameters.Add("prefix", SpannerDbType.String, prefix);
        }

        using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            var metadataJson = reader.IsDBNull(4) ? null : reader.GetFieldValue<string>(4);
            Dictionary<string, string>? customMetadata = null;
            if (!string.IsNullOrEmpty(metadataJson))
            {
                customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
            }

            yield return new StorageObjectMetadata
            {
                Key = reader.GetFieldValue<string>(0),
                Size = reader.GetFieldValue<long>(1),
                ContentType = reader.IsDBNull(2) ? null : reader.GetFieldValue<string>(2),
                ETag = reader.GetFieldValue<string>(3),
                CustomMetadata = customMetadata,
                Created = reader.GetFieldValue<DateTime>(5),
                Modified = reader.GetFieldValue<DateTime>(6),
                Tier = Tier
            };
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        using var cmd = _connection!.CreateSelectCommand(
            $"SELECT Size, ContentType, ETag, Metadata, CreatedAt, ModifiedAt FROM {_tableName} WHERE StorageKey = @key");
        cmd.Parameters.Add("key", SpannerDbType.String, key);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var metadataJson = reader.IsDBNull(3) ? null : reader.GetFieldValue<string>(3);
        Dictionary<string, string>? customMetadata = null;
        if (!string.IsNullOrEmpty(metadataJson))
        {
            customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
        }

        return new StorageObjectMetadata
        {
            Key = key,
            Size = reader.GetFieldValue<long>(0),
            ContentType = reader.IsDBNull(1) ? null : reader.GetFieldValue<string>(1),
            ETag = reader.GetFieldValue<string>(2),
            CustomMetadata = customMetadata,
            Created = reader.GetFieldValue<DateTime>(4),
            Modified = reader.GetFieldValue<DateTime>(5),
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            using var cmd = _connection!.CreateSelectCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryCoreAsync(
        string query, IDictionary<string, object>? parameters, CancellationToken ct)
    {
        using var cmd = _connection!.CreateSelectCommand(query);

        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                cmd.Parameters.Add(param.Key, GetSpannerType(param.Value), param.Value);
            }
        }

        var results = new List<Dictionary<string, object?>>();
        using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            results.Add(row);
        }

        return results;
    }

    protected override async Task<int> ExecuteNonQueryCoreAsync(
        string command, IDictionary<string, object>? parameters, CancellationToken ct)
    {
        using var cmd = _connection!.CreateDmlCommand(command);

        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                cmd.Parameters.Add(param.Key, GetSpannerType(param.Value), param.Value);
            }
        }

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(IsolationLevel isolationLevel, CancellationToken ct)
    {
        var transaction = await _connection!.BeginTransactionAsync(ct);
        return new SpannerDatabaseTransaction(transaction);
    }

    private static SpannerDbType GetSpannerType(object? value)
    {
        return value switch
        {
            string => SpannerDbType.String,
            int or long => SpannerDbType.Int64,
            double or float => SpannerDbType.Float64,
            bool => SpannerDbType.Bool,
            byte[] => SpannerDbType.Bytes,
            DateTime => SpannerDbType.Timestamp,
            _ => SpannerDbType.String
        };
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (_connection != null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }
        await base.DisposeAsyncCore();
    }

    private sealed class SpannerDatabaseTransaction : IDatabaseTransaction
    {
        private readonly Google.Cloud.Spanner.Data.SpannerTransaction _transaction;
        private bool _disposed;

        public string TransactionId { get; } = Guid.NewGuid().ToString("N");
        public IsolationLevel IsolationLevel => IsolationLevel.Serializable;

        public SpannerDatabaseTransaction(Google.Cloud.Spanner.Data.SpannerTransaction transaction)
        {
            _transaction = transaction;
        }

        public async Task CommitAsync(CancellationToken ct = default)
        {
            await _transaction.CommitAsync(ct);
        }

        public async Task RollbackAsync(CancellationToken ct = default)
        {
            await _transaction.RollbackAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _transaction.DisposeAsync();
        }
    }
}
