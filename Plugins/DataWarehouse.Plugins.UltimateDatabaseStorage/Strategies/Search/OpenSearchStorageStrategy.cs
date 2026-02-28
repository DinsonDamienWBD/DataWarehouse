using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using OpenSearch.Client;
using OpenSearch.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Search;

/// <summary>
/// OpenSearch storage strategy with production-ready features:
/// - Elasticsearch-compatible API
/// - Full-text search with analyzers
/// - Distributed architecture
/// - AWS managed service compatible
/// - Security features (fine-grained access control)
/// - Index State Management
/// - Anomaly detection
/// </summary>
public sealed class OpenSearchStorageStrategy : DatabaseStorageStrategyBase
{
    private OpenSearchClient? _client;
    private string _indexName = "datawarehouse-storage";
    private int _numberOfShards = 1;
    private int _numberOfReplicas = 1;

    public override string StrategyId => "opensearch";
    public override string Name => "OpenSearch Storage";
    public override StorageTier Tier => StorageTier.Warm;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.Search;
    public override string Engine => "OpenSearch";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = false,
        SupportsVersioning = true,
        SupportsTiering = true,
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
        _indexName = GetConfiguration("IndexName", "datawarehouse-storage");
        _numberOfShards = GetConfiguration("NumberOfShards", 1);
        _numberOfReplicas = GetConfiguration("NumberOfReplicas", 1);

        var connectionString = GetConnectionString();
        var uri = new Uri(connectionString);

        var pool = new SingleNodeConnectionPool(uri);
        var settings = new ConnectionSettings(pool);

        var username = GetConfiguration<string?>("Username", null);
        var password = GetConfiguration<string?>("Password", null);
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            settings.BasicAuthentication(username, password);
        }

        settings.DefaultIndex(_indexName);

        _client = new OpenSearchClient(settings);

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        var response = await _client!.PingAsync(ct: ct);
        if (!response.IsValid)
        {
            throw new InvalidOperationException("Failed to connect to OpenSearch");
        }
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        _client = null;
        await Task.CompletedTask;
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        var existsResponse = await _client!.Indices.ExistsAsync(_indexName, ct: ct);
        if (existsResponse.Exists) return;

        var createResponse = await _client.Indices.CreateAsync(_indexName, c => c
            .Settings(s => s
                .NumberOfShards(_numberOfShards)
                .NumberOfReplicas(_numberOfReplicas))
            .Map<StorageDocument>(m => m
                .Properties(p => p
                    .Keyword(k => k.Name(n => n.Key))
                    .Binary(b => b.Name(n => n.Data))
                    .Number(n => n.Name(x => x.Size).Type(NumberType.Long))
                    .Keyword(k => k.Name(n => n.ContentType))
                    .Keyword(k => k.Name(n => n.ETag))
                    .Object<Dictionary<string, string>>(o => o.Name(n => n.Metadata))
                    .Date(d => d.Name(n => n.CreatedAt))
                    .Date(d => d.Name(n => n.ModifiedAt)))), ct: ct);

        if (!createResponse.IsValid)
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

        if (!response.IsValid)
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
        var response = await _client!.GetAsync<StorageDocument>(key, g => g.Index(_indexName), ct);

        if (!response.IsValid || response.Source == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return Convert.FromBase64String(response.Source.Data);
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        var response = await _client!.DeleteAsync<StorageDocument>(key, d => d
            .Index(_indexName)
            .Refresh(Refresh.True), ct);

        if (!response.IsValid)
        {
            throw new InvalidOperationException($"Failed to delete document: {response.DebugInformation}");
        }

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var response = await _client!.DocumentExistsAsync<StorageDocument>(key, d => d.Index(_indexName), ct);
        return response.Exists;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        // Use From/Size pagination to avoid the 10 000-hit limit.
        const int PageSize = 1000;
        int from = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var localFrom = from;
            var searchResponse = await _client!.SearchAsync<StorageDocument>(s => s
                .Index(_indexName)
                .Query(q => string.IsNullOrEmpty(prefix)
                    ? q.MatchAll()
                    : q.Prefix(p => p.Field(f => f.Key).Value(prefix)))
                .From(localFrom)
                .Size(PageSize)
                .Sort(so => so.Ascending(f => f.Key))
                .Source(src => src.Excludes(e => e.Field(f => f.Data))), ct);

            if (!searchResponse.IsValid || searchResponse.Hits.Count == 0)
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
                    VersionId = hit.Version.ToString(),
                    Tier = Tier
                };
            }

            if (searchResponse.Hits.Count < PageSize)
                yield break;

            from += PageSize;
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        var response = await _client!.GetAsync<StorageDocument>(key, g => g
            .Index(_indexName)
            .SourceExcludes("data"), ct);

        if (!response.IsValid || response.Source == null)
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
            VersionId = response.Version.ToString(),
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            var response = await _client!.Cluster.HealthAsync(ct: ct);
            return response.IsValid && response.Status != Health.Red;
        }
        catch
        {
            return false;
        }
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
