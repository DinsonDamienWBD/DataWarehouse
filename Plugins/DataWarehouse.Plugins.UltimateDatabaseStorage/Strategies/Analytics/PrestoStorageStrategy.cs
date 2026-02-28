using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using System.Data.Common;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Analytics;

/// <summary>
/// Presto/Trino SQL analytics storage strategy with production-ready features:
/// - Distributed SQL query engine
/// - Federated queries across data sources
/// - ANSI SQL support
/// - Connector ecosystem
/// - Cost-based optimizer
/// - Fault-tolerant execution
/// - High concurrency
/// </summary>
public sealed class PrestoStorageStrategy : DatabaseStorageStrategyBase
{
    private HttpClient? _httpClient;
    private string _catalog = "memory";
    private string _schema = "default";
    private string _tableName = "storage";

    public override string StrategyId => "presto";
    public override string Name => "Presto/Trino SQL Analytics";
    public override StorageTier Tier => StorageTier.Warm;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.Analytics;
    public override string Engine => "Presto";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = false,
        SupportsVersioning = false,
        SupportsTiering = true, // Via connectors
        SupportsEncryption = true,
        SupportsCompression = true,
        SupportsMultipart = false,
        MaxObjectSize = 100L * 1024 * 1024,
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Eventual
    };

    public override bool SupportsTransactions => false;
    public override bool SupportsSql => true;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _catalog = GetConfiguration("Catalog", "memory");
        ValidateSqlIdentifier(_catalog, nameof(_catalog));
        _schema = GetConfiguration("Schema", "default");
        ValidateSqlIdentifier(_schema, nameof(_schema));
        _tableName = GetConfiguration("TableName", "storage");
        ValidateSqlIdentifier(_tableName, nameof(_tableName));

        var connectionString = GetConnectionString();
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(connectionString),
            Timeout = TimeSpan.FromSeconds(300)
        };

        var user = GetConfiguration("User", "datawarehouse");
        _httpClient.DefaultRequestHeaders.Remove("X-Presto-User");
        _httpClient.DefaultRequestHeaders.Add("X-Presto-User", user);
        _httpClient.DefaultRequestHeaders.Remove("X-Trino-User");
        _httpClient.DefaultRequestHeaders.Add("X-Trino-User", user);

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        var response = await _httpClient!.GetAsync("/v1/cluster", ct);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        _httpClient?.Dispose();
        _httpClient = null;
        await Task.CompletedTask;
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        await ExecuteQueryAsync($@"
            CREATE TABLE IF NOT EXISTS {_catalog}.{_schema}.{_tableName} (
                key VARCHAR,
                data VARCHAR,
                size BIGINT,
                content_type VARCHAR,
                etag VARCHAR,
                metadata VARCHAR,
                created_at TIMESTAMP,
                modified_at TIMESTAMP
            )", ct);
    }

    /// <summary>
    /// Escapes a string value for safe inclusion in Presto/Trino SQL literals.
    /// Replaces single quotes with doubled single quotes.
    /// </summary>
    private static string EscapeSqlString(string value)
    {
        return value.Replace("'", "''");
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var dataBase64 = Convert.ToBase64String(data);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : "";

        await ExecuteQueryAsync($@"
            INSERT INTO {_catalog}.{_schema}.{_tableName}
            (key, data, size, content_type, etag, metadata, created_at, modified_at)
            VALUES ('{EscapeSqlString(key)}', '{EscapeSqlString(dataBase64)}', {data.LongLength}, '{EscapeSqlString(contentType ?? "")}',
                    '{EscapeSqlString(etag)}', '{EscapeSqlString(metadataJson)}', TIMESTAMP '{now:yyyy-MM-dd HH:mm:ss.fff}',
                    TIMESTAMP '{now:yyyy-MM-dd HH:mm:ss.fff}')", ct);

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
        var results = await ExecuteQueryAsync($@"
            SELECT data FROM {_catalog}.{_schema}.{_tableName}
            WHERE key = '{EscapeSqlString(key)}'
            ORDER BY modified_at DESC
            LIMIT 1", ct);

        var row = results.FirstOrDefault();
        if (row == null || row.Count == 0)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var dataBase64 = row[0]?.ToString() ?? "";
        return Convert.FromBase64String(dataBase64);
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        await ExecuteQueryAsync($@"
            DELETE FROM {_catalog}.{_schema}.{_tableName}
            WHERE key = '{EscapeSqlString(key)}'", ct);

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var results = await ExecuteQueryAsync($@"
            SELECT 1 FROM {_catalog}.{_schema}.{_tableName}
            WHERE key = '{EscapeSqlString(key)}'
            LIMIT 1", ct);

        return results.Any();
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var whereClause = string.IsNullOrEmpty(prefix) ? "" : $"WHERE key LIKE '{EscapeSqlString(prefix)}%'";

        var results = await ExecuteQueryAsync($@"
            SELECT key, size, content_type, etag, metadata, created_at, modified_at
            FROM {_catalog}.{_schema}.{_tableName}
            {whereClause}
            ORDER BY key", ct);

        foreach (var row in results)
        {
            ct.ThrowIfCancellationRequested();

            if (row.Count < 7) continue;

            var key = row[0]?.ToString();
            if (string.IsNullOrEmpty(key)) continue;

            Dictionary<string, string>? customMetadata = null;
            var metadataJson = row[4]?.ToString();
            if (!string.IsNullOrEmpty(metadataJson))
            {
                customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
            }

            yield return new StorageObjectMetadata
            {
                Key = key,
                Size = Convert.ToInt64(row[1] ?? 0),
                ContentType = row[2]?.ToString(),
                ETag = row[3]?.ToString(),
                CustomMetadata = customMetadata,
                Created = DateTime.TryParse(row[5]?.ToString(), out var created) ? created : DateTime.UtcNow,
                Modified = DateTime.TryParse(row[6]?.ToString(), out var modified) ? modified : DateTime.UtcNow,
                Tier = Tier
            };
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        var results = await ExecuteQueryAsync($@"
            SELECT size, content_type, etag, metadata, created_at, modified_at
            FROM {_catalog}.{_schema}.{_tableName}
            WHERE key = '{EscapeSqlString(key)}'
            ORDER BY modified_at DESC
            LIMIT 1", ct);

        var row = results.FirstOrDefault();
        if (row == null || row.Count < 6)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        Dictionary<string, string>? customMetadata = null;
        var metadataJson = row[3]?.ToString();
        if (!string.IsNullOrEmpty(metadataJson))
        {
            customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
        }

        return new StorageObjectMetadata
        {
            Key = key,
            Size = Convert.ToInt64(row[0] ?? 0),
            ContentType = row[1]?.ToString(),
            ETag = row[2]?.ToString(),
            CustomMetadata = customMetadata,
            Created = DateTime.TryParse(row[4]?.ToString(), out var created) ? created : DateTime.UtcNow,
            Modified = DateTime.TryParse(row[5]?.ToString(), out var modified) ? modified : DateTime.UtcNow,
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            var response = await _httpClient!.GetAsync("/v1/cluster", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<List<List<object?>>> ExecuteQueryAsync(string sql, CancellationToken ct)
    {
        var results = new List<List<object?>>();

        var content = new StringContent(sql, Encoding.UTF8, "text/plain");
        _httpClient!.DefaultRequestHeaders.Remove("X-Presto-Catalog");
        _httpClient.DefaultRequestHeaders.Add("X-Presto-Catalog", _catalog);
        _httpClient.DefaultRequestHeaders.Remove("X-Presto-Schema");
        _httpClient.DefaultRequestHeaders.Add("X-Presto-Schema", _schema);

        var response = await _httpClient.PostAsync("/v1/statement", content, ct);
        response.EnsureSuccessStatusCode();

        var queryResult = await response.Content.ReadFromJsonAsync<PrestoQueryResult>(cancellationToken: ct);

        while (queryResult?.NextUri != null)
        {
            if (queryResult.Data != null)
            {
                results.AddRange(queryResult.Data);
            }

            await Task.Delay(100, ct);
            response = await _httpClient.GetAsync(queryResult.NextUri, ct);
            queryResult = await response.Content.ReadFromJsonAsync<PrestoQueryResult>(cancellationToken: ct);
        }

        if (queryResult?.Data != null)
        {
            results.AddRange(queryResult.Data);
        }

        return results;
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _httpClient?.Dispose();
        await base.DisposeAsyncCore();
    }

    private sealed class PrestoQueryResult
    {
        [JsonPropertyName("nextUri")]
        public string? NextUri { get; set; }

        [JsonPropertyName("data")]
        public List<List<object?>>? Data { get; set; }
    }
}
