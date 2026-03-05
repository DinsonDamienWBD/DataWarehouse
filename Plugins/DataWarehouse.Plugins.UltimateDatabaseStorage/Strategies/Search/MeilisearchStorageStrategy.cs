using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using Meilisearch;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Search;

/// <summary>
/// Meilisearch storage strategy with production-ready features:
/// - Typo-tolerant full-text search
/// - Instant search experience
/// - RESTful API
/// - Easy to deploy and maintain
/// - Filtering and faceting
/// - Customizable ranking
/// - Multi-tenancy support
/// </summary>
public sealed class MeilisearchStorageStrategy : DatabaseStorageStrategyBase
{
    private MeilisearchClient? _client;
    private Meilisearch.Index? _index;
    private string _indexName = "storage";

    public override string StrategyId => "meilisearch";
    public override string Name => "Meilisearch Storage";
    public override StorageTier Tier => StorageTier.Hot;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.Search;
    public override string Engine => "Meilisearch";

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
        MaxObjectSize = 100L * 1024 * 1024, // 100MB practical limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Eventual
    };

    public override bool SupportsTransactions => false;
    public override bool SupportsSql => false;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _indexName = GetConfiguration("IndexName", "storage");

        var connectionString = GetConnectionString();
        var apiKey = GetConfiguration<string?>("ApiKey", null);

        _client = new MeilisearchClient(connectionString, apiKey);

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        var health = await _client!.HealthAsync(ct);
        if (health.Status != "available")
        {
            throw new InvalidOperationException("Meilisearch is not healthy");
        }
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        _client = null;
        _index = null;
        await Task.CompletedTask;
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        try
        {
            _index = await _client!.GetIndexAsync(_indexName, ct);
        }
        catch
        {
            var task = await _client!.CreateIndexAsync(_indexName, "key", ct);
            await _client.WaitForTaskAsync(task.TaskUid, cancellationToken: ct);
            _index = await _client.GetIndexAsync(_indexName, ct);
        }

        // Configure filterable and sortable attributes
        await _index.UpdateFilterableAttributesAsync(new[] { "contentType", "size", "createdAt", "modifiedAt" }, ct);
        await _index.UpdateSortableAttributesAsync(new[] { "key", "size", "createdAt", "modifiedAt" }, ct);
        await _index.UpdateSearchableAttributesAsync(new[] { "key", "contentType" }, ct);
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);

        var document = new StorageDocument
        {
            Key = key,
            Data = Convert.ToBase64String(data),
            Size = data.LongLength,
            ContentType = contentType ?? "application/octet-stream",
            ETag = etag,
            Metadata = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : null,
            CreatedAt = now.Ticks,
            ModifiedAt = now.Ticks
        };

        var task = await _index!.AddDocumentsAsync(new[] { document }, "key", ct);
        await _client!.WaitForTaskAsync(task.TaskUid, cancellationToken: ct);

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
        try
        {
            var document = await _index!.GetDocumentAsync<StorageDocument>(key, cancellationToken: ct);
            if (document == null)
            {
                throw new FileNotFoundException($"Object not found: {key}");
            }
            return Convert.FromBase64String(document.Data);
        }
        catch (MeilisearchApiError)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        var task = await _index!.DeleteOneDocumentAsync(key, ct);
        await _client!.WaitForTaskAsync(task.TaskUid, cancellationToken: ct);

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        try
        {
            var document = await _index!.GetDocumentAsync<StorageDocument>(key, cancellationToken: ct);
            return document != null;
        }
        catch (MeilisearchApiError)
        {
            return false;
        }
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        // Paginate using Offset + Limit to avoid silently truncating beyond 1000 docs.
        const int PageSize = 1000;
        int offset = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // P2-2838: Restrict search to 'key' field and use MatchingStrategy.All so every
            // prefix token must appear in the key field. Post-filter still applied below for
            // exact ordinal prefix match to eliminate false positives from typo-tolerance.
            var searchParams = new SearchQuery
            {
                Limit = PageSize,
                Offset = offset,
                AttributesToRetrieve = new[] { "key", "size", "contentType", "etag", "metadata", "createdAt", "modifiedAt" },
                AttributesToSearchOn = new[] { "key" },
                MatchingStrategy = "all"
            };

            var result = await _index!.SearchAsync<StorageDocument>(prefix ?? "*", searchParams, ct);

            if (result.Hits.Count == 0)
                yield break;

            foreach (var hit in result.Hits)
            {
                ct.ThrowIfCancellationRequested();

                if (!string.IsNullOrEmpty(prefix) && !hit.Key.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                Dictionary<string, string>? customMetadata = null;
                if (!string.IsNullOrEmpty(hit.Metadata))
                    customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(hit.Metadata, JsonOptions);

                yield return new StorageObjectMetadata
                {
                    Key = hit.Key,
                    Size = hit.Size,
                    ContentType = hit.ContentType,
                    ETag = hit.ETag,
                    CustomMetadata = customMetadata,
                    Created = new DateTime(hit.CreatedAt, DateTimeKind.Utc),
                    Modified = new DateTime(hit.ModifiedAt, DateTimeKind.Utc),
                    Tier = Tier
                };
            }

            if (result.Hits.Count < PageSize)
                yield break;

            offset += PageSize;
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        try
        {
            var document = await _index!.GetDocumentAsync<StorageDocument>(key, cancellationToken: ct);
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
        catch (MeilisearchApiError)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            var health = await _client!.HealthAsync(ct);
            return health.Status == "available";
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
        var result = await _index!.SearchAsync<StorageDocument>(query, new SearchQuery
        {
            Limit = limit,
            AttributesToRetrieve = new[] { "key", "size", "contentType", "etag", "metadata", "createdAt", "modifiedAt" }
        }, ct);

        return result.Hits.Select(hit =>
        {
            Dictionary<string, string>? customMetadata = null;
            if (!string.IsNullOrEmpty(hit.Metadata))
            {
                customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(hit.Metadata, JsonOptions);
            }

            return new StorageObjectMetadata
            {
                Key = hit.Key,
                Size = hit.Size,
                ContentType = hit.ContentType,
                ETag = hit.ETag,
                CustomMetadata = customMetadata,
                Created = new DateTime(hit.CreatedAt, DateTimeKind.Utc),
                Modified = new DateTime(hit.ModifiedAt, DateTimeKind.Utc),
                Tier = Tier
            };
        });
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _client = null;
        _index = null;
        await base.DisposeAsyncCore();
    }

    private sealed class StorageDocument
    {
        public string Key { get; set; } = "";
        public string Data { get; set; } = "";
        public long Size { get; set; }
        public string ContentType { get; set; } = "";
        public string? ETag { get; set; }
        public string? Metadata { get; set; }
        public long CreatedAt { get; set; }
        public long ModifiedAt { get; set; }
    }
}
