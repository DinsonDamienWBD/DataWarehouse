using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Search;

/// <summary>
/// Typesense storage strategy with production-ready features:
/// - Fast full-text search
/// - Typo tolerance
/// - Tunable ranking
/// - Faceting and filtering
/// - High availability clustering
/// - In-memory engine for speed
/// - RESTful API
/// </summary>
public sealed class TypesenseStorageStrategy : DatabaseStorageStrategyBase
{
    private HttpClient? _httpClient;
    private string _collectionName = "storage";
    private string _apiKey = "";

    public override string StrategyId => "typesense";
    public override string Name => "Typesense Storage";
    public override StorageTier Tier => StorageTier.Hot;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.Search;
    public override string Engine => "Typesense";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = false,
        SupportsVersioning = false,
        SupportsTiering = false,
        SupportsEncryption = true,
        SupportsCompression = false,
        SupportsMultipart = false,
        MaxObjectSize = 100L * 1024 * 1024,
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Strong
    };

    public override bool SupportsTransactions => false;
    public override bool SupportsSql => false;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _collectionName = GetConfiguration("CollectionName", "storage");
        _apiKey = GetConfiguration<string?>("ApiKey", null)
            ?? throw new InvalidOperationException("ApiKey is required");

        var connectionString = GetConnectionString();
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(connectionString),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Remove("X-TYPESENSE-API-KEY");
        _httpClient.DefaultRequestHeaders.Add("X-TYPESENSE-API-KEY", _apiKey);

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        var response = await _httpClient!.GetAsync("/health", ct);
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
        // Check if collection exists
        var checkResponse = await _httpClient!.GetAsync($"/collections/{_collectionName}", ct);
        if (checkResponse.IsSuccessStatusCode) return;

        // Create collection
        var schema = new
        {
            name = _collectionName,
            fields = new object[]
            {
                new { name = "key", type = "string" },
                new { name = "data", type = "string", index = false },
                new { name = "size", type = "int64" },
                new { name = "contentType", type = "string", facet = true },
                new { name = "etag", type = "string" },
                new { name = "metadata", type = "string", optional = true },
                new { name = "createdAt", type = "int64" },
                new { name = "modifiedAt", type = "int64" }
            },
            default_sorting_field = "modifiedAt"
        };

        var content = new StringContent(JsonSerializer.Serialize(schema), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("/collections", content, ct);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);

        var document = new TypesenseDocument
        {
            Id = key,
            Key = key,
            Data = Convert.ToBase64String(data),
            Size = data.LongLength,
            ContentType = contentType ?? "application/octet-stream",
            ETag = etag,
            Metadata = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : null,
            CreatedAt = now.Ticks,
            ModifiedAt = now.Ticks
        };

        var content = new StringContent(JsonSerializer.Serialize(document), Encoding.UTF8, "application/json");
        var response = await _httpClient!.PostAsync(
            $"/collections/{_collectionName}/documents?action=upsert",
            content, ct);

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
        var response = await _httpClient!.GetAsync($"/collections/{_collectionName}/documents/{Uri.EscapeDataString(key)}", ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var document = await response.Content.ReadFromJsonAsync<TypesenseDocument>(cancellationToken: ct);
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
            $"/collections/{_collectionName}/documents/{Uri.EscapeDataString(key)}", ct);

        response.EnsureSuccessStatusCode();

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var response = await _httpClient!.GetAsync(
            $"/collections/{_collectionName}/documents/{Uri.EscapeDataString(key)}", ct);
        return response.IsSuccessStatusCode;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var query = string.IsNullOrEmpty(prefix) ? "*" : $"key:{prefix}*";
        var response = await _httpClient!.GetAsync(
            $"/collections/{_collectionName}/documents/search?q={Uri.EscapeDataString(query)}&query_by=key&per_page=250&exclude_fields=data", ct);

        if (!response.IsSuccessStatusCode)
        {
            yield break;
        }

        var result = await response.Content.ReadFromJsonAsync<TypesenseSearchResult>(cancellationToken: ct);

        foreach (var hit in result?.Hits ?? Enumerable.Empty<TypesenseHit>())
        {
            ct.ThrowIfCancellationRequested();

            var doc = hit.Document;
            if (doc == null) continue;

            if (!string.IsNullOrEmpty(prefix) && !doc.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, string>? customMetadata = null;
            if (!string.IsNullOrEmpty(doc.Metadata))
            {
                customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(doc.Metadata, JsonOptions);
            }

            yield return new StorageObjectMetadata
            {
                Key = doc.Key,
                Size = doc.Size,
                ContentType = doc.ContentType,
                ETag = doc.ETag,
                CustomMetadata = customMetadata,
                Created = new DateTime(doc.CreatedAt, DateTimeKind.Utc),
                Modified = new DateTime(doc.ModifiedAt, DateTimeKind.Utc),
                Tier = Tier
            };
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        var response = await _httpClient!.GetAsync(
            $"/collections/{_collectionName}/documents/{Uri.EscapeDataString(key)}", ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var document = await response.Content.ReadFromJsonAsync<TypesenseDocument>(cancellationToken: ct);
        if (document == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        Dictionary<string, string>? customMetadata = null;
        if (!string.IsNullOrEmpty(document.Metadata))
        {
            customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(document.Metadata, JsonOptions);
        }

        return new StorageObjectMetadata
        {
            Key = document.Key,
            Size = document.Size,
            ContentType = document.ContentType,
            ETag = document.ETag,
            CustomMetadata = customMetadata,
            Created = new DateTime(document.CreatedAt, DateTimeKind.Utc),
            Modified = new DateTime(document.ModifiedAt, DateTimeKind.Utc),
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            var response = await _httpClient!.GetAsync("/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Performs a typo-tolerant search.
    /// </summary>
    public async Task<IEnumerable<StorageObjectMetadata>> SearchAsync(string query, int limit = 100, CancellationToken ct = default)
    {
        var response = await _httpClient!.GetAsync(
            $"/collections/{_collectionName}/documents/search?q={Uri.EscapeDataString(query)}&query_by=key,contentType&per_page={limit}&exclude_fields=data", ct);

        if (!response.IsSuccessStatusCode)
        {
            return Enumerable.Empty<StorageObjectMetadata>();
        }

        var result = await response.Content.ReadFromJsonAsync<TypesenseSearchResult>(cancellationToken: ct);

        return (result?.Hits ?? Enumerable.Empty<TypesenseHit>()).Select(hit =>
        {
            var doc = hit.Document!;
            Dictionary<string, string>? customMetadata = null;
            if (!string.IsNullOrEmpty(doc.Metadata))
            {
                customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(doc.Metadata, JsonOptions);
            }

            return new StorageObjectMetadata
            {
                Key = doc.Key,
                Size = doc.Size,
                ContentType = doc.ContentType,
                ETag = doc.ETag,
                CustomMetadata = customMetadata,
                Created = new DateTime(doc.CreatedAt, DateTimeKind.Utc),
                Modified = new DateTime(doc.ModifiedAt, DateTimeKind.Utc),
                Tier = Tier
            };
        });
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _httpClient?.Dispose();
        await base.DisposeAsyncCore();
    }

    private sealed class TypesenseDocument
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("key")]
        public string Key { get; set; } = "";

        [JsonPropertyName("data")]
        public string Data { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("contentType")]
        public string ContentType { get; set; } = "";

        [JsonPropertyName("etag")]
        public string? ETag { get; set; }

        [JsonPropertyName("metadata")]
        public string? Metadata { get; set; }

        [JsonPropertyName("createdAt")]
        public long CreatedAt { get; set; }

        [JsonPropertyName("modifiedAt")]
        public long ModifiedAt { get; set; }
    }

    private sealed class TypesenseSearchResult
    {
        [JsonPropertyName("hits")]
        public TypesenseHit[]? Hits { get; set; }
    }

    private sealed class TypesenseHit
    {
        [JsonPropertyName("document")]
        public TypesenseDocument? Document { get; set; }
    }
}
