using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using Npgsql;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.NewSQL;

/// <summary>
/// CockroachDB NewSQL storage strategy with production-ready features:
/// - Distributed SQL database
/// - PostgreSQL compatible
/// - Serializable transactions
/// - Geo-partitioning
/// - Automatic rebalancing
/// - Multi-region support
/// - Strong consistency
/// </summary>
public sealed class CockroachDbStorageStrategy : DatabaseStorageStrategyBase
{
    private NpgsqlDataSource? _dataSource;
    private string _tableName = "storage";

    public override string StrategyId => "cockroachdb";
    public override string Name => "CockroachDB NewSQL Storage";
    public override StorageTier Tier => StorageTier.Hot;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.NewSQL;
    public override string Engine => "CockroachDB";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = true,
        SupportsVersioning = false,
        SupportsTiering = true, // Locality-aware
        SupportsEncryption = true,
        SupportsCompression = true,
        SupportsMultipart = false,
        MaxObjectSize = 64L * 1024 * 1024, // 64MB practical limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Strong
    };

    public override bool SupportsTransactions => true;
    public override bool SupportsSql => true;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _tableName = GetConfiguration("TableName", "storage");
        ValidateSqlIdentifier(_tableName, nameof(_tableName));

        var connectionString = GetConnectionString();
        _dataSource = NpgsqlDataSource.Create(connectionString);

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version()";
        var version = await command.ExecuteScalarAsync(ct);
        if (!version?.ToString()?.Contains("CockroachDB") ?? true)
        {
            throw new InvalidOperationException("Not connected to CockroachDB");
        }
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        if (_dataSource != null)
        {
            await _dataSource.DisposeAsync();
            _dataSource = null;
        }
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = $@"
            CREATE TABLE IF NOT EXISTS {_tableName} (
                key STRING PRIMARY KEY,
                data BYTES NOT NULL,
                size INT8 NOT NULL,
                content_type STRING,
                etag STRING NOT NULL,
                metadata JSONB,
                created_at TIMESTAMPTZ DEFAULT now(),
                modified_at TIMESTAMPTZ DEFAULT now(),
                INDEX idx_created_at (created_at),
                FAMILY data_family (data),
                FAMILY meta_family (key, size, content_type, etag, metadata, created_at, modified_at)
            )";

        await command.ExecuteNonQueryAsync(ct);
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : null;

        await using var connection = await _dataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();

        command.CommandText = $@"
            UPSERT INTO {_tableName} (key, data, size, content_type, etag, metadata, created_at, modified_at)
            VALUES (@key, @data, @size, @contentType, @etag, @metadata::jsonb, @createdAt, @modifiedAt)";

        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@data", data);
        command.Parameters.AddWithValue("@size", data.LongLength);
        command.Parameters.AddWithValue("@contentType", (object?)contentType ?? DBNull.Value);
        command.Parameters.AddWithValue("@etag", etag);
        command.Parameters.AddWithValue("@metadata", (object?)metadataJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@createdAt", now);
        command.Parameters.AddWithValue("@modifiedAt", now);

        await command.ExecuteNonQueryAsync(ct);

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
        await using var connection = await _dataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT data FROM {_tableName} WHERE key = @key";
        command.Parameters.AddWithValue("@key", key);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return (byte[])reader["data"];
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        await using var connection = await _dataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();

        command.CommandText = $"DELETE FROM {_tableName} WHERE key = @key";
        command.Parameters.AddWithValue("@key", key);

        await command.ExecuteNonQueryAsync(ct);

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT 1 FROM {_tableName} WHERE key = @key";
        command.Parameters.AddWithValue("@key", key);

        var result = await command.ExecuteScalarAsync(ct);
        return result != null;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();

        command.CommandText = $@"
            SELECT key, size, content_type, etag, metadata, created_at, modified_at
            FROM {_tableName}
            {(string.IsNullOrEmpty(prefix) ? "" : "WHERE key LIKE @prefix")}
            ORDER BY key";

        if (!string.IsNullOrEmpty(prefix))
        {
            command.Parameters.AddWithValue("@prefix", prefix + "%");
        }

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            var metadataJson = reader.IsDBNull(4) ? null : reader.GetString(4);
            Dictionary<string, string>? customMetadata = null;
            if (!string.IsNullOrEmpty(metadataJson))
            {
                customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
            }

            yield return new StorageObjectMetadata
            {
                Key = reader.GetString(0),
                Size = reader.GetInt64(1),
                ContentType = reader.IsDBNull(2) ? null : reader.GetString(2),
                ETag = reader.GetString(3),
                CustomMetadata = customMetadata,
                Created = reader.GetDateTime(5),
                Modified = reader.GetDateTime(6),
                Tier = Tier
            };
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();

        command.CommandText = $@"
            SELECT size, content_type, etag, metadata, created_at, modified_at
            FROM {_tableName} WHERE key = @key";

        command.Parameters.AddWithValue("@key", key);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var metadataJson = reader.IsDBNull(3) ? null : reader.GetString(3);
        Dictionary<string, string>? customMetadata = null;
        if (!string.IsNullOrEmpty(metadataJson))
        {
            customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
        }

        return new StorageObjectMetadata
        {
            Key = key,
            Size = reader.GetInt64(0),
            ContentType = reader.IsDBNull(1) ? null : reader.GetString(1),
            ETag = reader.GetString(2),
            CustomMetadata = customMetadata,
            Created = reader.GetDateTime(4),
            Modified = reader.GetDateTime(5),
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = await _dataSource!.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (_dataSource != null)
        {
            await _dataSource.DisposeAsync();
        }
        await base.DisposeAsyncCore();
    }
}
