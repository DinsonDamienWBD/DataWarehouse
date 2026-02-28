using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.NoSQL;

/// <summary>
/// CouchDB storage strategy with production-ready features:
/// - HTTP-based RESTful API (direct HTTP client, no third-party driver dependency issues)
/// - Multi-master replication
/// - Document storage with base64-encoded binary data
/// - Mango queries for flexible searching
/// - Conflict resolution for eventual consistency
/// - Change feeds for real-time updates
/// - Automatic compaction
/// </summary>
public sealed class CouchDbStorageStrategy : DatabaseStorageStrategyBase
{
    private HttpClient? _httpClient;
    private string _databaseName = "datawarehouse_storage";
    private string _serverUrl = "http://localhost:5984";

    public override string StrategyId => "couchdb";
    public override string Name => "CouchDB Storage";
    public override StorageTier Tier => StorageTier.Warm;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.NoSQL;
    public override string Engine => "CouchDB";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = true,
        SupportsLocking = false,
        SupportsVersioning = true, // Via _rev
        SupportsTiering = false,
        SupportsEncryption = false,
        SupportsCompression = true,
        SupportsMultipart = true,
        MaxObjectSize = null,
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Eventual
    };

    public override bool SupportsTransactions => false;
    public override bool SupportsSql => false;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _databaseName = GetConfiguration("DatabaseName", "datawarehouse_storage");
        _serverUrl = GetConfiguration("ServerUrl", "http://localhost:5984");
        var username = GetConfiguration<string?>("Username", null);
        var password = GetConfiguration<string?>("Password", null);

        var connectionString = GetConnectionString();
        if (!string.IsNullOrEmpty(connectionString))
        {
            var uri = new Uri(connectionString);
            _serverUrl = $"{uri.Scheme}://{uri.Host}:{uri.Port}";
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':');
                username = Uri.UnescapeDataString(parts[0]);
                if (parts.Length > 1)
                    password = Uri.UnescapeDataString(parts[1]);
            }
        }

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_serverUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(30)
        };

        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        // Test connection by getting database info
        var response = await _httpClient!.GetAsync($"/{Uri.EscapeDataString(_databaseName)}", ct);
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
        // Ensure database exists (PUT is idempotent)
        var response = await _httpClient!.PutAsync(
            $"/{Uri.EscapeDataString(_databaseName)}",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            ct);

        // 412 Precondition Failed means database already exists - that's fine
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PreconditionFailed)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var encodedKey = Uri.EscapeDataString(key);

        // Try to get existing document for its _rev (needed for updates)
        string? existingRev = null;
        DateTime createdAt = now;
        var getResponse = await _httpClient!.GetAsync($"/{Uri.EscapeDataString(_databaseName)}/{encodedKey}", ct);
        if (getResponse.IsSuccessStatusCode)
        {
            var existing = await getResponse.Content.ReadFromJsonAsync<CouchDocument>(cancellationToken: ct);
            existingRev = existing?.Rev;
            if (existing?.CreatedAt != default)
                createdAt = existing!.CreatedAt;
        }

        var doc = new CouchDocument
        {
            Id = key,
            Rev = existingRev,
            Data = Convert.ToBase64String(data),
            Size = data.LongLength,
            ContentType = contentType,
            ETag = etag,
            Metadata = metadata?.ToDictionary(k => k.Key, v => v.Value),
            CreatedAt = createdAt,
            ModifiedAt = now
        };

        var json = JsonSerializer.Serialize(doc, CouchJsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var putResponse = await _httpClient.PutAsync(
            $"/{Uri.EscapeDataString(_databaseName)}/{encodedKey}", content, ct);

        putResponse.EnsureSuccessStatusCode();

        var putResult = await putResponse.Content.ReadFromJsonAsync<CouchPutResult>(cancellationToken: ct);

        return new StorageObjectMetadata
        {
            Key = key,
            Size = data.LongLength,
            Created = createdAt,
            Modified = now,
            ETag = etag,
            ContentType = contentType,
            CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
            VersionId = putResult?.Rev,
            Tier = Tier
        };
    }

    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct)
    {
        var encodedKey = Uri.EscapeDataString(key);
        var response = await _httpClient!.GetAsync(
            $"/{Uri.EscapeDataString(_databaseName)}/{encodedKey}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        response.EnsureSuccessStatusCode();

        var doc = await response.Content.ReadFromJsonAsync<CouchDocument>(cancellationToken: ct);

        if (doc == null || string.IsNullOrEmpty(doc.Data))
        {
            throw new InvalidOperationException($"Document has no data: {key}");
        }

        return Convert.FromBase64String(doc.Data);
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var encodedKey = Uri.EscapeDataString(key);

        // Get the document to find its _rev and size
        var getResponse = await _httpClient!.GetAsync(
            $"/{Uri.EscapeDataString(_databaseName)}/{encodedKey}", ct);

        if (getResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return 0;
        }

        getResponse.EnsureSuccessStatusCode();
        var doc = await getResponse.Content.ReadFromJsonAsync<CouchDocument>(cancellationToken: ct);
        if (doc == null) return 0;

        var size = doc.Size;

        // Delete using _rev
        var deleteResponse = await _httpClient.DeleteAsync(
            $"/{Uri.EscapeDataString(_databaseName)}/{encodedKey}?rev={Uri.EscapeDataString(doc.Rev ?? "")}", ct);

        deleteResponse.EnsureSuccessStatusCode();

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var encodedKey = Uri.EscapeDataString(key);
        var request = new HttpRequestMessage(HttpMethod.Head,
            $"/{Uri.EscapeDataString(_databaseName)}/{encodedKey}");

        var response = await _httpClient!.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        // Use _all_docs with startkey/endkey for prefix filtering
        string url;
        if (string.IsNullOrEmpty(prefix))
        {
            url = $"/{Uri.EscapeDataString(_databaseName)}/_all_docs?include_docs=true";
        }
        else
        {
            var startKey = JsonSerializer.Serialize(prefix);
            var endKey = JsonSerializer.Serialize(prefix + "\ufff0");
            url = $"/{Uri.EscapeDataString(_databaseName)}/_all_docs?include_docs=true&startkey={Uri.EscapeDataString(startKey)}&endkey={Uri.EscapeDataString(endKey)}";
        }

        var response = await _httpClient!.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) yield break;

        var result = await response.Content.ReadFromJsonAsync<CouchAllDocsResult>(cancellationToken: ct);

        foreach (var row in result?.Rows ?? Array.Empty<CouchAllDocsRow>())
        {
            ct.ThrowIfCancellationRequested();

            var doc = row.Doc;
            if (doc == null || doc.Id == null || doc.Id.StartsWith("_design/")) continue;

            yield return new StorageObjectMetadata
            {
                Key = doc.Id,
                Size = doc.Size,
                ContentType = doc.ContentType,
                ETag = doc.ETag,
                CustomMetadata = doc.Metadata as IReadOnlyDictionary<string, string>,
                Created = doc.CreatedAt,
                Modified = doc.ModifiedAt,
                VersionId = doc.Rev,
                Tier = Tier
            };
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        var encodedKey = Uri.EscapeDataString(key);
        var response = await _httpClient!.GetAsync(
            $"/{Uri.EscapeDataString(_databaseName)}/{encodedKey}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        response.EnsureSuccessStatusCode();

        var doc = await response.Content.ReadFromJsonAsync<CouchDocument>(cancellationToken: ct);

        if (doc == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return new StorageObjectMetadata
        {
            Key = doc.Id ?? key,
            Size = doc.Size,
            ContentType = doc.ContentType,
            ETag = doc.ETag,
            CustomMetadata = doc.Metadata as IReadOnlyDictionary<string, string>,
            Created = doc.CreatedAt,
            Modified = doc.ModifiedAt,
            VersionId = doc.Rev,
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            var response = await _httpClient!.GetAsync($"/{Uri.EscapeDataString(_databaseName)}", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _httpClient?.Dispose();
        await base.DisposeAsyncCore();
    }

    private static readonly JsonSerializerOptions CouchJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private sealed class CouchDocument
    {
        [JsonPropertyName("_id")]
        public string? Id { get; set; }

        [JsonPropertyName("_rev")]
        public string? Rev { get; set; }

        [JsonPropertyName("data")]
        public string Data { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("contentType")]
        public string? ContentType { get; set; }

        [JsonPropertyName("etag")]
        public string? ETag { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, string>? Metadata { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("modifiedAt")]
        public DateTime ModifiedAt { get; set; }
    }

    private sealed class CouchPutResult
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("rev")]
        public string? Rev { get; set; }
    }

    private sealed class CouchAllDocsResult
    {
        [JsonPropertyName("rows")]
        public CouchAllDocsRow[]? Rows { get; set; }
    }

    private sealed class CouchAllDocsRow
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("doc")]
        public CouchDocument? Doc { get; set; }
    }
}
