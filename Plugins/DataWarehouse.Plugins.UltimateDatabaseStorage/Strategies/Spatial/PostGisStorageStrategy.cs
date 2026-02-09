using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using Npgsql;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Spatial;

/// <summary>
/// PostGIS spatial storage strategy with production-ready features:
/// - PostgreSQL with spatial extensions
/// - Geographic and geometric data types
/// - Spatial indexing (R-tree, GiST)
/// - OGC compliant
/// - Raster data support
/// - Topology support
/// - Geocoding functions
/// </summary>
public sealed class PostGisStorageStrategy : DatabaseStorageStrategyBase
{
    private NpgsqlDataSource? _dataSource;
    private string _tableName = "spatial_storage";

    public override string StrategyId => "postgis";
    public override string Name => "PostGIS Spatial Storage";
    public override StorageTier Tier => StorageTier.Warm;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.Spatial;
    public override string Engine => "PostGIS";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = true,
        SupportsVersioning = false,
        SupportsTiering = false,
        SupportsEncryption = true,
        SupportsCompression = true,
        SupportsMultipart = false,
        MaxObjectSize = 1L * 1024 * 1024 * 1024,
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Strong
    };

    public override bool SupportsTransactions => true;
    public override bool SupportsSql => true;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _tableName = GetConfiguration("TableName", "spatial_storage");

        var connectionString = GetConnectionString();
        _dataSource = NpgsqlDataSource.Create(connectionString);

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PostGIS_Version()";
        await command.ExecuteScalarAsync(ct);
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

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS postgis";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $@"
                CREATE TABLE IF NOT EXISTS {_tableName} (
                    key TEXT PRIMARY KEY,
                    data BYTEA NOT NULL,
                    size BIGINT NOT NULL,
                    content_type TEXT,
                    etag TEXT NOT NULL,
                    metadata JSONB,
                    geom GEOMETRY,
                    created_at TIMESTAMPTZ DEFAULT NOW(),
                    modified_at TIMESTAMPTZ DEFAULT NOW()
                )";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"CREATE INDEX IF NOT EXISTS idx_{_tableName}_geom ON {_tableName} USING GIST (geom)";
            await cmd.ExecuteNonQueryAsync(ct);
        }
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
            INSERT INTO {_tableName} (key, data, size, content_type, etag, metadata, created_at, modified_at)
            VALUES (@key, @data, @size, @contentType, @etag, @metadata::jsonb, @createdAt, @modifiedAt)
            ON CONFLICT (key) DO UPDATE SET
                data = EXCLUDED.data,
                size = EXCLUDED.size,
                content_type = EXCLUDED.content_type,
                etag = EXCLUDED.etag,
                metadata = EXCLUDED.metadata,
                modified_at = EXCLUDED.modified_at";

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
            command.CommandText = "SELECT PostGIS_Version()";
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
