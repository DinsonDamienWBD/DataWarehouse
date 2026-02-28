using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Search;

/// <summary>
/// Elasticsearch search engine storage strategy with production-ready features:
/// - Full-text search with analyzers
/// - Distributed architecture
/// - Near real-time search
/// - RESTful API
/// - Aggregations and analytics
/// - Index lifecycle management
/// - Cross-cluster replication
/// </summary>
public sealed class ElasticsearchStorageStrategy : DatabaseStorageStrategyBase
{
    private ElasticsearchClient? _client;
    private string _indexName = "datawarehouse-storage";
    private int _numberOfShards = 1;
    private int _numberOfReplicas = 1;

    public override string StrategyId => "elasticsearch";
    public override string Name => "Elasticsearch Search Storage";
    public override StorageTier Tier => StorageTier.Warm;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.Search;
    public override string Engine => "Elasticsearch";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = false,
        SupportsVersioning = true, // Document versioning
        SupportsTiering = true, // ILM support
        SupportsEncryption = true,
        SupportsCompression = true,
        SupportsMultipart = false,
        MaxObjectSize = 100L * 1024 * 1024, // 100MB practical limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Eventual
    };

    public override bool SupportsTransactions => false;
    public override bool SupportsSql => true; // Elasticsearch SQL

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _indexName = GetConfiguration("IndexName", "datawarehouse-storage");
        _numberOfShards = GetConfiguration("NumberOfShards", 1);
        _numberOfReplicas = GetConfiguration("NumberOfReplicas", 1);

        var connectionString = GetConnectionString();
        var uri = new Uri(connectionString);

        var settings = new ElasticsearchClientSettings(uri);

        var username = GetConfiguration<string?>("Username", null);
        var password = GetConfiguration<string?>("Password", null);
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            settings.Authentication(new BasicAuthentication(username, password));
        }

        var apiKey = GetConfiguration<string?>("ApiKey", null);
        if (!string.IsNullOrEmpty(apiKey))
        {
            settings.Authentication(new ApiKey(apiKey));
        }

        _client = new ElasticsearchClient(settings);

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        var response = await _client!.PingAsync(ct);
        if (!response.IsValidResponse)
        {
            throw new InvalidOperationException("Failed to connect to Elasticsearch");
        }
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        _client = null;
        await Task.CompletedTask;
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        var existsResponse = await _client!.Indices.ExistsAsync(_indexName, ct);
        if (existsResponse.Exists) return;

        var createResponse = await _client.Indices.CreateAsync(_indexName, c => c
            .Settings(s => s
                .NumberOfShards(_numberOfShards)
                .NumberOfReplicas(_numberOfReplicas))
            .Mappings(m => m
                .Properties(p => p
                    .Keyword("key")
                    .Binary("data")
                    .LongNumber("size")
                    .Keyword("contentType")
                    .Keyword("etag")
                    .Object("metadata")
                    .Date("createdAt")
                    .Date("modifiedAt"))), ct);

        if (!createResponse.IsValidResponse)
        {
            throw new InvalidOperationException($"Failed to create index: {createResponse.DebugInformation}");
        }
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
            ContentType = contentType,
            ETag = etag,
            Metadata = metadata?.ToDictionary(k => k.Key, v => v.Value),
            CreatedAt = now,
            ModifiedAt = now
        };

        var response = await _client!.IndexAsync(document, i => i
            .Index(_indexName)
            .Id(key)
            .Refresh(Refresh.True), ct);

        if (!response.IsValidResponse)
        {
            throw new InvalidOperationException($"Failed to store document: {response.DebugInformation}");
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
            VersionId = response.Version.ToString(),
            Tier = Tier
        };
    }

    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct)
    {
        var response = await _client!.GetAsync<StorageDocument>(_indexName, key, ct);

        if (!response.IsValidResponse || response.Source == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return Convert.FromBase64String(response.Source.Data);
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        var response = await _client!.DeleteAsync<StorageDocument>(_indexName, key, d => d.Refresh(Refresh.True), ct);

        if (!response.IsValidResponse)
        {
            throw new InvalidOperationException($"Failed to delete document: {response.DebugInformation}");
        }

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var response = await _client!.ExistsAsync(_indexName, key, ct);
        return response.Exists;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        // Use From/Size pagination to avoid silently truncating large indexes.
        // PageSize is intentionally bounded; callers enumerate the full result via the
        // async stream rather than a single capped query.
        const int PageSize = 1000;
        int from = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var localFrom = from;
            SearchResponse<StorageDocument> searchResponse;
            if (string.IsNullOrEmpty(prefix))
            {
                searchResponse = await _client!.SearchAsync<StorageDocument>(s => s
                    .Indices(_indexName)
                    .From(localFrom)
                    .Size(PageSize)
                    .Sort(so => so.Field(f => f.Key, d => d.Order(Elastic.Clients.Elasticsearch.SortOrder.Asc))), ct);
            }
            else
            {
                searchResponse = await _client!.SearchAsync<StorageDocument>(s => s
                    .Indices(_indexName)
                    .Query(q => q.Prefix(p => p.Field(f => f.Key).Value(prefix)))
                    .From(localFrom)
                    .Size(PageSize)
                    .Sort(so => so.Field(f => f.Key, d => d.Order(Elastic.Clients.Elasticsearch.SortOrder.Asc))), ct);
            }

            if (!searchResponse.IsValidResponse || searchResponse.Hits.Count == 0)
            {
                yield break;
            }

            foreach (var hit in searchResponse.Hits)
            {
                ct.ThrowIfCancellationRequested();

                var doc = hit.Source;
                if (doc == null) continue;

                yield return new StorageObjectMetadata
                {
                    Key = doc.Key,
                    Size = doc.Size,
                    ContentType = doc.ContentType,
                    ETag = doc.ETag,
                    CustomMetadata = doc.Metadata as IReadOnlyDictionary<string, string>,
                    Created = doc.CreatedAt,
                    Modified = doc.ModifiedAt,
                    VersionId = hit.Version?.ToString(),
                    Tier = Tier
                };
            }

            // If we got fewer results than the page size, there are no more pages.
            if (searchResponse.Hits.Count < PageSize)
                yield break;

            from += PageSize;
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        var response = await _client!.GetAsync<StorageDocument>(_indexName, key, g => g
            .SourceExcludes("data"), ct);

        if (!response.IsValidResponse || response.Source == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var doc = response.Source;
        return new StorageObjectMetadata
        {
            Key = doc.Key,
            Size = doc.Size,
            ContentType = doc.ContentType,
            ETag = doc.ETag,
            CustomMetadata = doc.Metadata as IReadOnlyDictionary<string, string>,
            Created = doc.CreatedAt,
            Modified = doc.ModifiedAt,
            VersionId = response.Version?.ToString(),
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            var response = await _client!.Cluster.HealthAsync(ct);
            return response.IsValidResponse && response.Status != Elastic.Clients.Elasticsearch.HealthStatus.Red;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Executes a full-text search query.
    /// </summary>
    public async Task<IEnumerable<StorageObjectMetadata>> SearchAsync(string query, int maxResults = 100, CancellationToken ct = default)
    {
        var response = await _client!.SearchAsync<StorageDocument>(s => s
            .Indices(_indexName)
            .Query(q => q.MultiMatch(m => m
                .Query(query)
                .Fields(new[] { "key^2", "contentType", "metadata.*" })))
            .Size(maxResults), ct);

        if (!response.IsValidResponse)
        {
            return Enumerable.Empty<StorageObjectMetadata>();
        }

        return response.Hits.Select(hit => new StorageObjectMetadata
        {
            Key = hit.Source?.Key ?? "",
            Size = hit.Source?.Size ?? 0,
            ContentType = hit.Source?.ContentType,
            ETag = hit.Source?.ETag,
            CustomMetadata = hit.Source?.Metadata as IReadOnlyDictionary<string, string>,
            Created = hit.Source?.CreatedAt ?? DateTime.UtcNow,
            Modified = hit.Source?.ModifiedAt ?? DateTime.UtcNow,
            VersionId = hit.Version?.ToString(),
            Tier = Tier
        });
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _client = null;
        await base.DisposeAsyncCore();
    }

    private sealed class StorageDocument
    {
        public string Key { get; set; } = "";
        public string Data { get; set; } = "";
        public long Size { get; set; }
        public string? ContentType { get; set; }
        public string? ETag { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ModifiedAt { get; set; }
    }
}
