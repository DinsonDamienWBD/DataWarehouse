using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Graph;

/// <summary>
/// ArangoDB multi-model storage strategy with production-ready features:
/// - Graph, document, and key-value in one database
/// - AQL query language
/// - ACID transactions
/// - Flexible schema
/// - Graph traversals
/// - Full-text search
/// - Geo-spatial queries
/// </summary>
public sealed class ArangoDbStorageStrategy : DatabaseStorageStrategyBase
{
    private HttpClient? _httpClient;
    private string _database = "datawarehouse";
    private string _collection = "storage";
    private string? _jwtToken;

    public override string StrategyId => "arangodb";
    public override string Name => "ArangoDB Multi-Model Storage";
    public override StorageTier Tier => StorageTier.Warm;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.Graph;
    public override string Engine => "ArangoDB";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = false,
        SupportsVersioning = true, // Document revisions
        SupportsTiering = false,
        SupportsEncryption = true,
        SupportsCompression = true,
        SupportsMultipart = false,
        MaxObjectSize = 512L * 1024 * 1024, // 512MB document limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Strong
    };

    public override bool SupportsTransactions => true;
    public override bool SupportsSql => false; // Uses AQL

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _database = GetConfiguration("Database", "datawarehouse");
        _collection = GetConfiguration("Collection", "storage");

        var connectionString = GetConnectionString();
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(connectionString),
            Timeout = TimeSpan.FromSeconds(30)
        };

        var username = GetConfiguration<string?>("Username", null);
        var password = GetConfiguration<string?>("Password", null);

        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            await AuthenticateAsync(username, password, ct);
        }

        await EnsureSchemaCoreAsync(ct);
    }

    private async Task AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(new { username, password }),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient!.PostAsync("/_open/auth", content, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AuthResult>(cancellationToken: ct);
        _jwtToken = result?.Jwt;

        if (!string.IsNullOrEmpty(_jwtToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _jwtToken);
        }
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        var response = await _httpClient!.GetAsync("/_api/version", ct);
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
        // Create database if not exists
        var dbContent = new StringContent(
            JsonSerializer.Serialize(new { name = _database }),
            Encoding.UTF8,
            "application/json");

        var dbResponse = await _httpClient!.PostAsync("/_api/database", dbContent, ct);
        // Ignore if database already exists

        // Create collection if not exists
        var collContent = new StringContent(
            JsonSerializer.Serialize(new { name = _collection, type = 2 }),
            Encoding.UTF8,
            "application/json");

        await _httpClient.PostAsync($"/_db/{_database}/_api/collection", collContent, ct);

        // Create index on key
        var indexContent = new StringContent(
            JsonSerializer.Serialize(new
            {
                type = "persistent",
                fields = new[] { "key" },
                unique = true
            }),
            Encoding.UTF8,
            "application/json");

        await _httpClient.PostAsync($"/_db/{_database}/_api/index?collection={_collection}", indexContent, ct);
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);

        var document = new ArangoDocument
        {
            Key = key,
            Data = Convert.ToBase64String(data),
            Size = data.LongLength,
            ContentType = contentType,
            ETag = etag,
            Metadata = metadata?.ToDictionary(k => k.Key, v => v.Value),
            CreatedAt = now,
            ModifiedAt = now
        };

        var content = new StringContent(
            JsonSerializer.Serialize(document),
            Encoding.UTF8,
            "application/json");

        // Try to update, if not exists, create
        var response = await _httpClient!.PutAsync(
            $"/_db/{_database}/_api/document/{_collection}/{Uri.EscapeDataString(key)}",
            content, ct);

        if (!response.IsSuccessStatusCode)
        {
            response = await _httpClient.PostAsync(
                $"/_db/{_database}/_api/document/{_collection}",
                content, ct);
        }

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ArangoDocumentResult>(cancellationToken: ct);

        return new StorageObjectMetadata
        {
            Key = key,
            Size = data.LongLength,
            Created = now,
            Modified = now,
            ETag = etag,
            ContentType = contentType,
            CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
            VersionId = result?.Rev,
            Tier = Tier
        };
    }

    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct)
    {
        var response = await _httpClient!.GetAsync(
            $"/_db/{_database}/_api/document/{_collection}/{Uri.EscapeDataString(key)}", ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var document = await response.Content.ReadFromJsonAsync<ArangoDocument>(cancellationToken: ct);
        if (document == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return Convert.FromBase64String(document.Data);
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        var response = await _httpClient!.DeleteAsync(
            $"/_db/{_database}/_api/document/{_collection}/{Uri.EscapeDataString(key)}", ct);

        response.EnsureSuccessStatusCode();

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var response = await _httpClient!.GetAsync(
            $"/_db/{_database}/_api/document/{_collection}/{Uri.EscapeDataString(key)}", ct);
        return response.IsSuccessStatusCode;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var filter = string.IsNullOrEmpty(prefix)
            ? ""
            : $"FILTER STARTS_WITH(doc.key, @prefix)";

        var aql = $@"
            FOR doc IN {_collection}
            {filter}
            RETURN {{
                key: doc.key,
                size: doc.size,
                contentType: doc.contentType,
                etag: doc.etag,
                metadata: doc.metadata,
                createdAt: doc.createdAt,
                modifiedAt: doc.modifiedAt,
                rev: doc._rev
            }}";

        var query = new
        {
            query = aql,
            bindVars = string.IsNullOrEmpty(prefix) ? null : new { prefix }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(query),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient!.PostAsync(
            $"/_db/{_database}/_api/cursor", content, ct);

        if (!response.IsSuccessStatusCode) yield break;

        var result = await response.Content.ReadFromJsonAsync<ArangoCursorResult>(cancellationToken: ct);

        foreach (var doc in result?.Result ?? Enumerable.Empty<ArangoListDocument>())
        {
            ct.ThrowIfCancellationRequested();

            yield return new StorageObjectMetadata
            {
                Key = doc.Key,
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
        var response = await _httpClient!.GetAsync(
            $"/_db/{_database}/_api/document/{_collection}/{Uri.EscapeDataString(key)}", ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var document = await response.Content.ReadFromJsonAsync<ArangoDocument>(cancellationToken: ct);
        if (document == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return new StorageObjectMetadata
        {
            Key = document.Key,
            Size = document.Size,
            ContentType = document.ContentType,
            ETag = document.ETag,
            CustomMetadata = document.Metadata as IReadOnlyDictionary<string, string>,
            Created = document.CreatedAt,
            Modified = document.ModifiedAt,
            VersionId = document.Rev,
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            var response = await _httpClient!.GetAsync("/_api/version", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(new
            {
                collections = new { write = new[] { _collection } }
            }),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient!.PostAsync(
            $"/_db/{_database}/_api/transaction/begin", content, ct);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ArangoTransactionResult>(cancellationToken: ct);

        return new ArangoTransaction(_httpClient, _database, result?.Result?.Id ?? "");
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _httpClient?.Dispose();
        await base.DisposeAsyncCore();
    }

    private sealed class AuthResult
    {
        [JsonPropertyName("jwt")]
        public string? Jwt { get; set; }
    }

    private sealed class ArangoDocument
    {
        [JsonPropertyName("_key")]
        public string Key { get; set; } = "";

        [JsonPropertyName("_rev")]
        public string? Rev { get; set; }

        [JsonPropertyName("data")]
        public string Data { get; set; } = "";

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

    private sealed class ArangoDocumentResult
    {
        [JsonPropertyName("_rev")]
        public string? Rev { get; set; }
    }

    private sealed class ArangoCursorResult
    {
        [JsonPropertyName("result")]
        public ArangoListDocument[]? Result { get; set; }
    }

    private sealed class ArangoListDocument
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = "";

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

        [JsonPropertyName("rev")]
        public string? Rev { get; set; }
    }

    private sealed class ArangoTransactionResult
    {
        [JsonPropertyName("result")]
        public ArangoTransactionInfo? Result { get; set; }
    }

    private sealed class ArangoTransactionInfo
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private sealed class ArangoTransaction : IDatabaseTransaction
    {
        private readonly HttpClient _client;
        private readonly string _database;
        private readonly string _transactionId;
        private bool _disposed;

        public string TransactionId => _transactionId;
        public System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.ReadCommitted;

        public ArangoTransaction(HttpClient client, string database, string transactionId)
        {
            _client = client;
            _database = database;
            _transactionId = transactionId;
        }

        public async Task CommitAsync(CancellationToken ct = default)
        {
            await _client.PutAsync(
                $"/_db/{_database}/_api/transaction/{_transactionId}", null, ct);
        }

        public async Task RollbackAsync(CancellationToken ct = default)
        {
            await _client.DeleteAsync(
                $"/_db/{_database}/_api/transaction/{_transactionId}", ct);
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
