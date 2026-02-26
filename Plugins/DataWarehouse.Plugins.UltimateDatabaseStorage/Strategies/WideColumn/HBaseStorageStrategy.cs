using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.WideColumn;

/// <summary>
/// Apache HBase wide-column storage strategy with production-ready features:
/// - Hadoop-based distributed storage
/// - Column-family organization
/// - Strong consistency
/// - Automatic sharding
/// - Versioned cells
/// - Bulk loading support
/// - Region-based scaling
/// </summary>
public sealed class HBaseStorageStrategy : DatabaseStorageStrategyBase
{
    private HttpClient? _httpClient;
    private string _namespace = "default";
    private string _tableName = "storage";
    private string _columnFamily = "cf";

    public override string StrategyId => "hbase";
    public override string Name => "HBase Wide-Column Storage";
    public override StorageTier Tier => StorageTier.Cold;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.WideColumn;
    public override string Engine => "HBase";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = false,
        SupportsVersioning = true, // HBase supports cell versioning
        SupportsTiering = true,
        SupportsEncryption = true,
        SupportsCompression = true,
        SupportsMultipart = false,
        MaxObjectSize = 10L * 1024 * 1024, // 10MB cell limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Strong
    };

    public override bool SupportsTransactions => false;
    public override bool SupportsSql => false;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _namespace = GetConfiguration("Namespace", "default");
        _tableName = GetConfiguration("TableName", "storage");
        _columnFamily = GetConfiguration("ColumnFamily", "cf");

        var connectionString = GetConnectionString();
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(connectionString),
            Timeout = TimeSpan.FromSeconds(GetConfiguration("TimeoutSeconds", 30))
        };

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        var response = await _httpClient!.GetAsync("/version/cluster", ct);
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
        var fullTableName = $"{_namespace}:{_tableName}";

        // Check if table exists
        var checkResponse = await _httpClient!.GetAsync($"/{fullTableName}/schema", ct);
        if (checkResponse.IsSuccessStatusCode) return;

        // Create table with column family
        var schema = new
        {
            name = fullTableName,
            ColumnSchema = new[]
            {
                new
                {
                    name = _columnFamily,
                    COMPRESSION = "LZ4",
                    VERSIONS = 3,
                    TTL = 2147483647 // Max TTL
                }
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(schema), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PutAsync($"/{fullTableName}/schema", content, ct);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var fullTableName = $"{_namespace}:{_tableName}";
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : "";

        var rowKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(key));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var cells = new
        {
            Row = new[]
            {
                new
                {
                    key = rowKey,
                    Cell = new[]
                    {
                        new { column = ToBase64($"{_columnFamily}:data"), timestamp, value = Convert.ToBase64String(data) },
                        new { column = ToBase64($"{_columnFamily}:size"), timestamp, value = ToBase64(data.LongLength.ToString()) },
                        new { column = ToBase64($"{_columnFamily}:contentType"), timestamp, value = ToBase64(contentType ?? "") },
                        new { column = ToBase64($"{_columnFamily}:etag"), timestamp, value = ToBase64(etag) },
                        new { column = ToBase64($"{_columnFamily}:metadata"), timestamp, value = ToBase64(metadataJson) },
                        new { column = ToBase64($"{_columnFamily}:createdAt"), timestamp, value = ToBase64(now.ToString("o")) },
                        new { column = ToBase64($"{_columnFamily}:modifiedAt"), timestamp, value = ToBase64(now.ToString("o")) }
                    }
                }
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(cells), Encoding.UTF8, "application/json");
        var response = await _httpClient!.PutAsync($"/{fullTableName}/{rowKey}", content, ct);
        response.EnsureSuccessStatusCode();

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
        var fullTableName = $"{_namespace}:{_tableName}";
        var rowKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(key));

        var response = await _httpClient!.GetAsync($"/{fullTableName}/{rowKey}/{_columnFamily}:data", ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var result = await response.Content.ReadFromJsonAsync<HBaseRowResponse>(cancellationToken: ct);
        var cell = result?.Row?.FirstOrDefault()?.Cell?.FirstOrDefault();

        if (cell == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return Convert.FromBase64String(cell.value);
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        var fullTableName = $"{_namespace}:{_tableName}";
        var rowKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(key));

        var response = await _httpClient!.DeleteAsync($"/{fullTableName}/{rowKey}", ct);
        response.EnsureSuccessStatusCode();

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var fullTableName = $"{_namespace}:{_tableName}";
        var rowKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(key));

        var response = await _httpClient!.GetAsync($"/{fullTableName}/{rowKey}", ct);
        return response.IsSuccessStatusCode;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var fullTableName = $"{_namespace}:{_tableName}";

        // Use scanner for listing
        var scannerRequest = new { batch = 100 };
        var scannerContent = new StringContent(JsonSerializer.Serialize(scannerRequest), Encoding.UTF8, "application/json");
        var scannerResponse = await _httpClient!.PutAsync($"/{fullTableName}/scanner", scannerContent, ct);
        scannerResponse.EnsureSuccessStatusCode();

        var scannerLocation = scannerResponse.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(scannerLocation)) yield break;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var batchResponse = await _httpClient.GetAsync(scannerLocation, ct);
                if (!batchResponse.IsSuccessStatusCode) break;

                var batch = await batchResponse.Content.ReadFromJsonAsync<HBaseRowResponse>(cancellationToken: ct);
                if (batch?.Row == null || batch.Row.Length == 0) break;

                foreach (var row in batch.Row)
                {
                    var key = Encoding.UTF8.GetString(Convert.FromBase64String(row.key));

                    if (!string.IsNullOrEmpty(prefix) && !key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var cells = row.Cell?.ToDictionary(
                        c => Encoding.UTF8.GetString(Convert.FromBase64String(c.column)),
                        c => c.value) ?? new Dictionary<string, string>();

                    long size = 0;
                    if (cells.TryGetValue($"{_columnFamily}:size", out var sizeB64))
                    {
                        long.TryParse(FromBase64(sizeB64), out size);
                    }

                    Dictionary<string, string>? customMetadata = null;
                    if (cells.TryGetValue($"{_columnFamily}:metadata", out var metaB64))
                    {
                        var metaJson = FromBase64(metaB64);
                        if (!string.IsNullOrEmpty(metaJson))
                        {
                            customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metaJson, JsonOptions);
                        }
                    }

                    yield return new StorageObjectMetadata
                    {
                        Key = key,
                        Size = size,
                        ContentType = cells.TryGetValue($"{_columnFamily}:contentType", out var ct2) ? FromBase64(ct2) : null,
                        ETag = cells.TryGetValue($"{_columnFamily}:etag", out var etag) ? FromBase64(etag) : null,
                        CustomMetadata = customMetadata,
                        Created = cells.TryGetValue($"{_columnFamily}:createdAt", out var created)
                            ? DateTime.Parse(FromBase64(created)) : DateTime.UtcNow,
                        Modified = cells.TryGetValue($"{_columnFamily}:modifiedAt", out var modified)
                            ? DateTime.Parse(FromBase64(modified)) : DateTime.UtcNow,
                        Tier = Tier
                    };
                }
            }
        }
        finally
        {
            await _httpClient.DeleteAsync(scannerLocation, ct);
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        var fullTableName = $"{_namespace}:{_tableName}";
        var rowKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(key));

        var response = await _httpClient!.GetAsync($"/{fullTableName}/{rowKey}", ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var result = await response.Content.ReadFromJsonAsync<HBaseRowResponse>(cancellationToken: ct);
        var row = result?.Row?.FirstOrDefault();

        if (row == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var cells = row.Cell?.ToDictionary(
            c => Encoding.UTF8.GetString(Convert.FromBase64String(c.column)),
            c => c.value) ?? new Dictionary<string, string>();

        long size = 0;
        if (cells.TryGetValue($"{_columnFamily}:size", out var sizeB64))
        {
            long.TryParse(FromBase64(sizeB64), out size);
        }

        Dictionary<string, string>? customMetadata = null;
        if (cells.TryGetValue($"{_columnFamily}:metadata", out var metaB64))
        {
            var metaJson = FromBase64(metaB64);
            if (!string.IsNullOrEmpty(metaJson))
            {
                customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metaJson, JsonOptions);
            }
        }

        return new StorageObjectMetadata
        {
            Key = key,
            Size = size,
            ContentType = cells.TryGetValue($"{_columnFamily}:contentType", out var contentType) ? FromBase64(contentType) : null,
            ETag = cells.TryGetValue($"{_columnFamily}:etag", out var etag) ? FromBase64(etag) : null,
            CustomMetadata = customMetadata,
            Created = cells.TryGetValue($"{_columnFamily}:createdAt", out var created)
                ? DateTime.Parse(FromBase64(created)) : DateTime.UtcNow,
            Modified = cells.TryGetValue($"{_columnFamily}:modifiedAt", out var modified)
                ? DateTime.Parse(FromBase64(modified)) : DateTime.UtcNow,
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            var response = await _httpClient!.GetAsync("/version/cluster", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string ToBase64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string FromBase64(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));

    protected override async ValueTask DisposeAsyncCore()
    {
        _httpClient?.Dispose();
        await base.DisposeAsyncCore();
    }

    private sealed class HBaseRowResponse
    {
        public HBaseRow[]? Row { get; set; }
    }

    private sealed class HBaseRow
    {
        public string key { get; set; } = "";
        public HBaseCell[]? Cell { get; set; }
    }

    private sealed class HBaseCell
    {
        public string column { get; set; } = "";
        public long timestamp { get; set; }
        public string value { get; set; } = "";
    }
}
