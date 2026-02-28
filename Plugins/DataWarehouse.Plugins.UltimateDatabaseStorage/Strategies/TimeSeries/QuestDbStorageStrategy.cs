using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using Npgsql;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.TimeSeries;

/// <summary>
/// QuestDB time-series storage strategy with production-ready features:
/// - High-performance time-series database
/// - Column-oriented storage
/// - SQL support with extensions
/// - InfluxDB line protocol support
/// - High ingestion rates (millions of rows/sec)
/// - Out-of-order ingestion
/// - Time-based partitioning
/// </summary>
public sealed class QuestDbStorageStrategy : DatabaseStorageStrategyBase
{
    private NpgsqlDataSource? _pgDataSource;
    private HttpClient? _httpClient;
    private string _tableName = "storage";
    private bool _useHttpForWrite = true;

    public override string StrategyId => "questdb";
    public override string Name => "QuestDB Time-Series Storage";
    public override StorageTier Tier => StorageTier.Hot;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.TimeSeries;
    public override string Engine => "QuestDB";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = false,
        SupportsVersioning = false,
        SupportsTiering = true,
        SupportsEncryption = false,
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
        _tableName = GetConfiguration("TableName", "storage");
        ValidateSqlIdentifier(_tableName, nameof(_tableName));
        _useHttpForWrite = GetConfiguration("UseHttpForWrite", true);

        var connectionString = GetConnectionString();

        // Parse connection string for PostgreSQL wire protocol
        var pgConnectionString = GetConfiguration("PgConnectionString", connectionString);
        _pgDataSource = NpgsqlDataSource.Create(pgConnectionString);

        // HTTP endpoint for writes
        var httpEndpoint = GetConfiguration("HttpEndpoint", connectionString.Replace(":8812", ":9000"));
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(httpEndpoint),
            Timeout = TimeSpan.FromSeconds(30)
        };

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        await using var connection = await _pgDataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(ct);
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        if (_pgDataSource != null)
        {
            await _pgDataSource.DisposeAsync();
            _pgDataSource = null;
        }
        _httpClient?.Dispose();
        _httpClient = null;
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        await using var connection = await _pgDataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();

        command.CommandText = $@"
            CREATE TABLE IF NOT EXISTS {_tableName} (
                key SYMBOL CAPACITY 100000 CACHE INDEX,
                data STRING,
                size LONG,
                content_type SYMBOL,
                etag STRING,
                metadata STRING,
                timestamp TIMESTAMP
            ) TIMESTAMP(timestamp) PARTITION BY DAY";

        await command.ExecuteNonQueryAsync(ct);
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : "";
        var dataBase64 = Convert.ToBase64String(data);

        if (_useHttpForWrite)
        {
            // Use ILP (InfluxDB Line Protocol) for high-performance writes
            var ilp = $"{_tableName},key={EscapeIlp(key)} " +
                     $"data=\"{EscapeIlpString(dataBase64)}\",size={data.LongLength}i," +
                     $"content_type=\"{EscapeIlpString(contentType ?? "")}\",etag=\"{EscapeIlpString(etag)}\"," +
                     $"metadata=\"{EscapeIlpString(metadataJson)}\" {now.Ticks * 100}\n";

            var content = new StringContent(ilp, Encoding.UTF8, "text/plain");
            var response = await _httpClient!.PostAsync("/write", content, ct);
            response.EnsureSuccessStatusCode();
        }
        else
        {
            await using var connection = await _pgDataSource!.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();

            command.CommandText = $@"
                INSERT INTO {_tableName} (key, data, size, content_type, etag, metadata, timestamp)
                VALUES ($1, $2, $3, $4, $5, $6, $7)";

            command.Parameters.AddWithValue(key);
            command.Parameters.AddWithValue(dataBase64);
            command.Parameters.AddWithValue(data.LongLength);
            command.Parameters.AddWithValue(contentType ?? "");
            command.Parameters.AddWithValue(etag);
            command.Parameters.AddWithValue(metadataJson);
            command.Parameters.AddWithValue(now);

            await command.ExecuteNonQueryAsync(ct);
        }

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
        await using var connection = await _pgDataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();

        command.CommandText = $@"
            SELECT data FROM {_tableName}
            WHERE key = $1
            ORDER BY timestamp DESC
            LIMIT 1";

        command.Parameters.AddWithValue(key);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var dataBase64 = reader.GetString(0);
        return Convert.FromBase64String(dataBase64);
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        // QuestDB does not support DELETE DML on individual rows. Data can only be
        // removed via TTL/retention policies or DROP TABLE. Log a warning so operators
        // are aware that the row is not immediately removed.
        var metadata = await GetMetadataCoreAsync(key, ct);
        System.Diagnostics.Debug.WriteLine(
            $"[Warning] QuestDbStorageStrategy: DELETE is not supported for key '{key}'. " +
            "The record will remain until the retention policy expires. Configure a " +
            "retention policy on bucket '{_tableName}' to enforce data lifecycle.");
        return metadata.Size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        await using var connection = await _pgDataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT 1 FROM {_tableName} WHERE key = $1 LIMIT 1";
        command.Parameters.AddWithValue(key);

        var result = await command.ExecuteScalarAsync(ct);
        return result != null;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await _pgDataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();

        var whereClause = string.IsNullOrEmpty(prefix) ? "" : "WHERE key ~ $1";

        command.CommandText = $@"
            SELECT DISTINCT key,
                   LAST(size) AS size,
                   LAST(content_type) AS content_type,
                   LAST(etag) AS etag,
                   LAST(metadata) AS metadata,
                   LAST(timestamp) AS timestamp
            FROM {_tableName}
            {whereClause}
            SAMPLE BY 1s ALIGN TO CALENDAR";

        if (!string.IsNullOrEmpty(prefix))
        {
            command.Parameters.AddWithValue("^" + prefix);
        }

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            var key = reader.GetString(0);
            var metadataJson = reader.IsDBNull(4) ? null : reader.GetString(4);

            Dictionary<string, string>? customMetadata = null;
            if (!string.IsNullOrEmpty(metadataJson))
            {
                customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
            }

            var time = reader.GetDateTime(5);

            yield return new StorageObjectMetadata
            {
                Key = key,
                Size = reader.GetInt64(1),
                ContentType = reader.IsDBNull(2) ? null : reader.GetString(2),
                ETag = reader.GetString(3),
                CustomMetadata = customMetadata,
                Created = time,
                Modified = time,
                Tier = Tier
            };
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        await using var connection = await _pgDataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();

        command.CommandText = $@"
            SELECT size, content_type, etag, metadata, timestamp
            FROM {_tableName}
            WHERE key = $1
            ORDER BY timestamp DESC
            LIMIT 1";

        command.Parameters.AddWithValue(key);

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

        var time = reader.GetDateTime(4);

        return new StorageObjectMetadata
        {
            Key = key,
            Size = reader.GetInt64(0),
            ContentType = reader.IsDBNull(1) ? null : reader.GetString(1),
            ETag = reader.GetString(2),
            CustomMetadata = customMetadata,
            Created = time,
            Modified = time,
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = await _pgDataSource!.OpenConnectionAsync(ct);
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

    private static string EscapeIlp(string value)
    {
        return value.Replace(" ", "\\ ").Replace(",", "\\,").Replace("=", "\\=");
    }

    private static string EscapeIlpString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (_pgDataSource != null)
        {
            await _pgDataSource.DisposeAsync();
        }
        _httpClient?.Dispose();
        await base.DisposeAsyncCore();
    }
}
