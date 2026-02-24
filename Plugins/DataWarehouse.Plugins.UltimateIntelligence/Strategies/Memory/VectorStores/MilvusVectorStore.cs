using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.VectorStores;

/// <summary>
/// Index types supported by Milvus.
/// </summary>
public enum MilvusIndexType
{
    /// <summary>Flat index (brute force, highest accuracy).</summary>
    Flat,

    /// <summary>IVF with flat quantization.</summary>
    IvfFlat,

    /// <summary>IVF with scalar quantization.</summary>
    IvfSq8,

    /// <summary>IVF with product quantization.</summary>
    IvfPq,

    /// <summary>Hierarchical Navigable Small World graphs.</summary>
    Hnsw,

    /// <summary>ANNOY index.</summary>
    Annoy,

    /// <summary>DiskANN for large-scale datasets.</summary>
    DiskAnn
}

/// <summary>
/// Configuration for Milvus vector store.
/// </summary>
public sealed class MilvusOptions : VectorStoreOptions
{
    /// <summary>Milvus host URL (e.g., "http://localhost:19530" or Zilliz Cloud endpoint).</summary>
    public string Host { get; init; } = "http://localhost:19530";

    /// <summary>Collection name.</summary>
    public string Collection { get; init; } = "datawarehouse";

    /// <summary>API key (for Zilliz Cloud).</summary>
    public string? ApiKey { get; init; }

    /// <summary>Partition name for data organization.</summary>
    public string? Partition { get; init; }

    /// <summary>Index type to use.</summary>
    public MilvusIndexType IndexType { get; init; } = MilvusIndexType.Hnsw;

    /// <summary>Number of clusters for IVF indexes.</summary>
    public int NList { get; init; } = 128;

    /// <summary>Search parameter for IVF indexes (number of clusters to probe).</summary>
    public int NProbe { get; init; } = 16;

    /// <summary>HNSW M parameter (number of bi-directional links).</summary>
    public int HnswM { get; init; } = 16;

    /// <summary>HNSW efConstruction parameter.</summary>
    public int HnswEfConstruction { get; init; } = 256;
}

/// <summary>
/// Production-ready Milvus distributed vector database connector.
/// </summary>
/// <remarks>
/// Milvus is a cloud-native vector database built for billion-scale similarity search.
/// Features:
/// <list type="bullet">
/// <item>Collections and partitions for data organization</item>
/// <item>Multiple index types: IVF_FLAT, IVF_SQ8, IVF_PQ, HNSW, ANNOY, DiskANN</item>
/// <item>Scalar filtering on indexed fields</item>
/// <item>Zilliz Cloud managed service available</item>
/// <item>GPU acceleration support</item>
/// </list>
/// </remarks>
public sealed class MilvusVectorStore : ProductionVectorStoreBase
{
    private readonly MilvusOptions _options;
    private int _dimensions;

    /// <inheritdoc/>
    public override string StoreId => $"milvus-{_options.Collection}";

    /// <inheritdoc/>
    public override string DisplayName => $"Milvus ({_options.Collection})";

    /// <inheritdoc/>
    public override int VectorDimensions => _dimensions;

    /// <summary>
    /// Creates a new Milvus vector store instance.
    /// </summary>
    /// <param name="options">Milvus configuration options.</param>
    /// <param name="httpClient">Optional HTTP client for testing.</param>
    public MilvusVectorStore(MilvusOptions options, HttpClient? httpClient = null)
        : base(httpClient, options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string endpoint)
    {
        var request = new HttpRequestMessage(method, $"{_options.Host.TrimEnd('/')}{endpoint}");
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            request.Headers.Add("Authorization", $"Bearer {_options.ApiKey}");
        }
        return request;
    }

    /// <inheritdoc/>
    public override async Task UpsertAsync(
        string id,
        float[] vector,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        await ExecuteWithResilienceAsync(async token =>
        {
            var data = new List<object>
            {
                new
                {
                    id,
                    vector,
                    metadata = metadata ?? new Dictionary<string, object>()
                }
            };

            var payload = new MilvusInsertRequest
            {
                CollectionName = _options.Collection,
                Data = data,
                PartitionName = _options.Partition
            };

            using var request = CreateRequest(HttpMethod.Post, "/v1/vector/insert");
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
        }, "Upsert", ct);
    }

    /// <inheritdoc/>
    public override async Task UpsertBatchAsync(IEnumerable<VectorRecord> records, CancellationToken ct = default)
    {
        foreach (var batch in BatchItems(records, _options.MaxBatchSize))
        {
            await ExecuteWithResilienceAsync(async token =>
            {
                var data = batch.Select(r => new
                {
                    id = r.Id,
                    vector = r.Vector,
                    metadata = r.Metadata ?? new Dictionary<string, object>()
                }).Cast<object>().ToList();

                var payload = new MilvusInsertRequest
                {
                    CollectionName = _options.Collection,
                    Data = data,
                    PartitionName = _options.Partition
                };

                using var request = CreateRequest(HttpMethod.Post, "/v1/vector/insert");
                request.Content = JsonContent.Create(payload, options: JsonOptions);

                using var response = await HttpClient.SendAsync(request, token);
                response.EnsureSuccessStatusCode();

                Metrics.RecordUpsert(TimeSpan.Zero, data.Count - 1);
            }, "UpsertBatch", ct);
        }
    }

    /// <inheritdoc/>
    public override async Task<VectorRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        return await ExecuteWithResilienceAsync(async token =>
        {
            var payload = new
            {
                collectionName = _options.Collection,
                id,
                outputFields = new[] { "*" }
            };

            using var request = CreateRequest(HttpMethod.Post, "/v1/vector/get");
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
                return null;

            var result = await response.Content.ReadFromJsonAsync<MilvusGetResponse>(JsonOptions, token);
            var data = result?.Data?.FirstOrDefault();

            if (data != null)
            {
                return new VectorRecord(
                    id,
                    data.Vector ?? Array.Empty<float>(),
                    data.Metadata);
            }
            return null;
        }, "Get", ct);
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ExecuteWithResilienceAsync(async token =>
        {
            var payload = new
            {
                collectionName = _options.Collection,
                id
            };

            using var request = CreateRequest(HttpMethod.Post, "/v1/vector/delete");
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            Metrics.RecordDelete();
        }, "Delete", ct);
    }

    /// <inheritdoc/>
    public override async Task DeleteBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        foreach (var batch in BatchItems(idList, _options.MaxBatchSize))
        {
            await ExecuteWithResilienceAsync(async token =>
            {
                var batchIds = batch.ToArray();
                var filter = $"id in [{string.Join(",", batchIds.Select(id => $"\"{id}\""))}]";

                var payload = new
                {
                    collectionName = _options.Collection,
                    filter
                };

                using var request = CreateRequest(HttpMethod.Post, "/v1/vector/delete");
                request.Content = JsonContent.Create(payload, options: JsonOptions);

                using var response = await HttpClient.SendAsync(request, token);
                response.EnsureSuccessStatusCode();

                Metrics.RecordDelete(batchIds.Length);
            }, "DeleteBatch", ct);
        }
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<VectorSearchResult>> SearchAsync(
        float[] query,
        int topK = 10,
        float minScore = 0f,
        Dictionary<string, object>? filter = null,
        CancellationToken ct = default)
    {
        return await ExecuteWithResilienceAsync(async token =>
        {
            var searchParams = GetSearchParams();

            var payload = new MilvusSearchRequest
            {
                CollectionName = _options.Collection,
                Vector = query,
                TopK = topK,
                OutputFields = new[] { "*" },
                Params = searchParams,
                Filter = filter != null ? BuildFilter(filter) : null,
                PartitionNames = !string.IsNullOrEmpty(_options.Partition)
                    ? new[] { _options.Partition }
                    : null
            };

            using var request = CreateRequest(HttpMethod.Post, "/v1/vector/search");
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<MilvusSearchResponse>(JsonOptions, token);

            return result?.Data?
                .Where(m => m.Score >= minScore)
                .Select(m => new VectorSearchResult(
                    m.Id ?? "",
                    m.Score,
                    _options.IncludeVectorsInSearch ? m.Vector : null,
                    m.Metadata))
                .ToList() ?? new List<VectorSearchResult>();
        }, "Search", ct);
    }

    private Dictionary<string, object> GetSearchParams()
    {
        return _options.IndexType switch
        {
            MilvusIndexType.IvfFlat or MilvusIndexType.IvfSq8 or MilvusIndexType.IvfPq =>
                new Dictionary<string, object> { ["nprobe"] = _options.NProbe },
            MilvusIndexType.Hnsw =>
                new Dictionary<string, object> { ["ef"] = _options.NProbe * 2 },
            MilvusIndexType.Annoy =>
                new Dictionary<string, object> { ["search_k"] = _options.NProbe * 10 },
            _ => new Dictionary<string, object>()
        };
    }

    private static string BuildFilter(Dictionary<string, object> filter)
    {
        var conditions = filter.Select(kvp =>
        {
            var value = kvp.Value is string s ? $"\"{s}\"" : kvp.Value.ToString();
            return $"metadata[\"{kvp.Key}\"] == {value}";
        });
        return string.Join(" and ", conditions);
    }

    /// <inheritdoc/>
    public override async Task CreateCollectionAsync(
        string name,
        int dimensions,
        DistanceMetric metric = DistanceMetric.Cosine,
        CancellationToken ct = default)
    {
        await ExecuteWithResilienceAsync(async token =>
        {
            var metricType = metric switch
            {
                DistanceMetric.Cosine => "COSINE",
                DistanceMetric.Euclidean => "L2",
                DistanceMetric.DotProduct => "IP",
                _ => "COSINE"
            };

            var indexParams = GetIndexParams();

            var payload = new
            {
                collectionName = name,
                dimension = dimensions,
                metricType,
                primaryField = "id",
                vectorField = "vector",
                description = "Created by DataWarehouse",
                indexParams
            };

            using var request = CreateRequest(HttpMethod.Post, "/v1/vector/collections/create");
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            _dimensions = dimensions;
        }, "CreateCollection", ct);
    }

    private Dictionary<string, object> GetIndexParams()
    {
        var indexType = _options.IndexType switch
        {
            MilvusIndexType.Flat => "FLAT",
            MilvusIndexType.IvfFlat => "IVF_FLAT",
            MilvusIndexType.IvfSq8 => "IVF_SQ8",
            MilvusIndexType.IvfPq => "IVF_PQ",
            MilvusIndexType.Hnsw => "HNSW",
            MilvusIndexType.Annoy => "ANNOY",
            MilvusIndexType.DiskAnn => "DISKANN",
            _ => "HNSW"
        };

        var parameters = new Dictionary<string, object> { ["index_type"] = indexType };

        switch (_options.IndexType)
        {
            case MilvusIndexType.IvfFlat:
            case MilvusIndexType.IvfSq8:
            case MilvusIndexType.IvfPq:
                parameters["nlist"] = _options.NList;
                break;
            case MilvusIndexType.Hnsw:
                parameters["M"] = _options.HnswM;
                parameters["efConstruction"] = _options.HnswEfConstruction;
                break;
        }

        return parameters;
    }

    /// <inheritdoc/>
    public override async Task DeleteCollectionAsync(string name, CancellationToken ct = default)
    {
        await ExecuteWithResilienceAsync(async token =>
        {
            var payload = new { collectionName = name };

            using var request = CreateRequest(HttpMethod.Post, "/v1/vector/collections/drop");
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
        }, "DeleteCollection", ct);
    }

    /// <inheritdoc/>
    public override async Task<bool> CollectionExistsAsync(string name, CancellationToken ct = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, $"/v1/vector/collections/describe?collectionName={Uri.EscapeDataString(name)}");
            using var response = await HttpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            Debug.WriteLine($"Caught exception in MilvusVectorStore.cs");
            return false;
        }
    }

    /// <inheritdoc/>
    public override async Task<VectorStoreStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        return await ExecuteWithResilienceAsync(async token =>
        {
            var url = $"/v1/vector/collections/describe?collectionName={Uri.EscapeDataString(_options.Collection)}";
            using var request = CreateRequest(HttpMethod.Get, url);
            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<MilvusDescribeResponse>(JsonOptions, token);
            var info = result?.Data;

            _dimensions = info?.Dimension ?? 0;

            return new VectorStoreStatistics
            {
                TotalVectors = info?.RowCount ?? 0,
                Dimensions = _dimensions,
                CollectionCount = 1,
                QueryCount = Metrics.QueryCount,
                AverageQueryLatencyMs = Metrics.AverageQueryLatencyMs,
                IndexType = _options.IndexType.ToString().ToUpperInvariant(),
                Metric = DistanceMetric.Cosine,
                ExtendedStats = new Dictionary<string, object>
                {
                    ["loadState"] = info?.LoadState ?? "unknown",
                    ["shardsNum"] = info?.ShardsNum ?? 0,
                    ["indexType"] = _options.IndexType.ToString()
                }
            };
        }, "GetStatistics", ct);
    }

    /// <inheritdoc/>
    public override async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var request = CreateRequest(HttpMethod.Get, "/v1/vector/collections");
            using var response = await HttpClient.SendAsync(request, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            Debug.WriteLine($"Caught exception in MilvusVectorStore.cs");
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #region Request/Response Models

    private sealed class MilvusInsertRequest
    {
        public string CollectionName { get; set; } = "";
        public List<object> Data { get; set; } = new();
        public string? PartitionName { get; set; }
    }

    private sealed class MilvusSearchRequest
    {
        public string CollectionName { get; set; } = "";
        public float[] Vector { get; set; } = Array.Empty<float>();
        public int TopK { get; set; }
        public string[] OutputFields { get; set; } = Array.Empty<string>();
        public Dictionary<string, object>? Params { get; set; }
        public string? Filter { get; set; }
        public string[]? PartitionNames { get; set; }
    }

    private sealed class MilvusSearchResponse
    {
        public List<MilvusSearchResult>? Data { get; set; }
    }

    private sealed class MilvusSearchResult
    {
        public string? Id { get; set; }
        public float Score { get; set; }
        public float[]? Vector { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }
    }

    private sealed class MilvusGetResponse
    {
        public List<MilvusData>? Data { get; set; }
    }

    private sealed class MilvusData
    {
        public string? Id { get; set; }
        public float[]? Vector { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }
    }

    private sealed class MilvusDescribeResponse
    {
        public MilvusCollectionInfo? Data { get; set; }
    }

    private sealed class MilvusCollectionInfo
    {
        public string? CollectionName { get; set; }
        public int Dimension { get; set; }
        public long RowCount { get; set; }
        public string? LoadState { get; set; }
        public int ShardsNum { get; set; }
    }

    #endregion
}
