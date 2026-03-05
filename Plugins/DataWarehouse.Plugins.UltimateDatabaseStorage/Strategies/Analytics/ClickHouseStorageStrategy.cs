using ClickHouse.Client.ADO;
using ClickHouse.Client.Utility;
using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Analytics;

/// <summary>
/// ClickHouse analytical storage strategy with production-ready features:
/// - Column-oriented OLAP database
/// - Real-time analytics
/// - SQL support with extensions
/// - High compression ratios
/// - Distributed queries
/// - Materialized views
/// - Time-series optimized
/// </summary>
public sealed class ClickHouseStorageStrategy : DatabaseStorageStrategyBase
{
    private ClickHouseConnection? _connection;
    private string _database = "datawarehouse";
    private string _tableName = "storage";

    public override string StrategyId => "clickhouse";
    public override string Name => "ClickHouse Analytics Storage";
    public override StorageTier Tier => StorageTier.Warm;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.Analytics;
    public override string Engine => "ClickHouse";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = false,
        SupportsVersioning = false,
        SupportsTiering = true, // TTL, storage policies
        SupportsEncryption = true,
        SupportsCompression = true,
        SupportsMultipart = false,
        MaxObjectSize = 100L * 1024 * 1024, // Practical limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Eventual
    };

    public override bool SupportsTransactions => false;
    public override bool SupportsSql => true;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _database = GetConfiguration("Database", "datawarehouse");
        _tableName = GetConfiguration("TableName", "storage");

        // P2-2773: Validate identifiers to prevent DDL injection via misconfigured values.
        ValidateSqlIdentifier(_database, nameof(_database));
        ValidateSqlIdentifier(_tableName, nameof(_tableName));

        var connectionString = GetConnectionString();
        _connection = new ClickHouseConnection(connectionString);

        await EnsureSchemaCoreAsync(ct);
    }

    /// <summary>
    /// Validates that an SQL identifier contains only safe characters (letters, digits, underscores).
    /// Throws <see cref="ArgumentException"/> if the identifier is invalid.
    /// </summary>
    private new static void ValidateSqlIdentifier(string identifier, string paramName)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException($"SQL identifier '{paramName}' must not be empty.", paramName);
        foreach (var ch in identifier)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_')
                throw new ArgumentException(
                    $"SQL identifier '{paramName}' contains invalid character '{ch}'. Only letters, digits and underscores are allowed.", paramName);
        }
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        if (_connection!.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync(ct);
        }
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        await _connection!.CloseAsync();
        _connection = null;
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        // Open only if not already open to avoid double-open when called from InitializeCoreAsync
        // before ConnectCoreAsync has run.
        bool openedHere = _connection!.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await _connection.OpenAsync(ct);
        }

        try
        {
            await using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS {_database}";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = $@"
                    CREATE TABLE IF NOT EXISTS {_database}.{_tableName} (
                        key String,
                        data String,
                        size Int64,
                        content_type Nullable(String),
                        etag String,
                        metadata Nullable(String),
                        created_at DateTime64(3),
                        modified_at DateTime64(3),
                        sign Int8 DEFAULT 1
                    ) ENGINE = CollapsingMergeTree(sign)
                    ORDER BY key
                    SETTINGS index_granularity = 8192";
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            // Only close if we opened; leave open if caller will manage state.
            if (openedHere)
            {
                await _connection.CloseAsync();
            }
        }
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var dataBase64 = Convert.ToBase64String(data);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : null;

        if (_connection!.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync(ct);
        }

        // Insert with CollapsingMergeTree pattern
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
            INSERT INTO {_database}.{_tableName}
            (key, data, size, content_type, etag, metadata, created_at, modified_at, sign)
            VALUES
            (@key, @data, @size, @contentType, @etag, @metadata, @createdAt, @modifiedAt, 1)";

        cmd.AddParameter("key", key);
        cmd.AddParameter("data", dataBase64);
        cmd.AddParameter("size", data.LongLength);
        cmd.AddParameter("contentType", contentType);
        cmd.AddParameter("etag", etag);
        cmd.AddParameter("metadata", metadataJson);
        cmd.AddParameter("createdAt", now);
        cmd.AddParameter("modifiedAt", now);

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
        if (_connection!.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync(ct);
        }

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT data FROM {_database}.{_tableName}
            WHERE key = @key AND sign = 1
            ORDER BY modified_at DESC
            LIMIT 1";

        cmd.AddParameter("key", key);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var dataBase64 = reader.GetString(0);
        return Convert.FromBase64String(dataBase64);
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        if (_connection!.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync(ct);
        }

        // Insert cancellation row for CollapsingMergeTree
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
            INSERT INTO {_database}.{_tableName}
            (key, data, size, content_type, etag, metadata, created_at, modified_at, sign)
            SELECT key, data, size, content_type, etag, metadata, created_at, now(), -1
            FROM {_database}.{_tableName}
            WHERE key = @key AND sign = 1";

        cmd.AddParameter("key", key);
        await cmd.ExecuteNonQueryAsync(ct);

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        if (_connection!.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync(ct);
        }

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT 1 FROM {_database}.{_tableName}
            WHERE key = @key AND sign = 1
            LIMIT 1";

        cmd.AddParameter("key", key);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result != null;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        if (_connection!.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync(ct);
        }

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT key, size, content_type, etag, metadata, created_at, modified_at
            FROM {_database}.{_tableName}
            WHERE sign = 1
            {(string.IsNullOrEmpty(prefix) ? "" : "AND startsWith(key, @prefix)")}
            ORDER BY key";

        if (!string.IsNullOrEmpty(prefix))
        {
            cmd.AddParameter("prefix", prefix);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);

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
        if (_connection!.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync(ct);
        }

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT size, content_type, etag, metadata, created_at, modified_at
            FROM {_database}.{_tableName}
            WHERE key = @key AND sign = 1
            ORDER BY modified_at DESC
            LIMIT 1";

        cmd.AddParameter("key", key);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
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
            if (_connection!.State != System.Data.ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
            }

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (_connection != null)
        {
            await _connection.CloseAsync();
        }
        await base.DisposeAsyncCore();
    }
}
