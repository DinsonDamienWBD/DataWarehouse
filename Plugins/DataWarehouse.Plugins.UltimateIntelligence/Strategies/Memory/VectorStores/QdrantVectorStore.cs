using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.VectorStores;

/// <summary>
/// Configuration for Qdrant vector store.
/// </summary>
public sealed class QdrantOptions : VectorStoreOptions
{
    /// <summary>Qdrant host URL (e.g., "http://localhost:6333" or "https://xxx.qdrant.io").</summary>
    public string Host { get; init; } = "http://localhost:6333";

    /// <summary>Collection name.</summary>
    public string Collection { get; init; } = "datawarehouse";

    /// <summary>API key for Qdrant Cloud or secured instances.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Whether to use gRPC instead of HTTP (not implemented in this version).</summary>
    public bool UseGrpc { get; init; } = false;

    /// <summary>Shard number for distributed deployments.</summary>
    public int? ShardNumber { get; init; }

    /// <summary>Write consistency level.</summary>
    public int? WriteConsistencyFactor { get; init; }
}

/// <summary>
/// Production-ready Qdrant vector search engine connector.
/// </summary>
/// <remarks>
/// Qdrant is a high-performance vector similarity search engine written in Rust.
/// Features:
/// <list type="bullet">
/// <item>HTTP and gRPC APIs</item>
/// <item>Rich filtering with payload indexes</item>
/// <item>Match, range, and geo conditions</item>
/// <item>Local and Qdrant Cloud deployment options</item>
/// <item>HNSW indexing with configurable parameters</item>
/// </list>
/// </remarks>
public sealed class QdrantVectorStore : ProductionVectorStoreBase
{
    private readonly QdrantOptions _options;
    private int _dimensions;

    /// <inheritdoc/>
    public override string StoreId => $"qdrant-{_options.Collection}";

    /// <inheritdoc/>
    public override string DisplayName => $"Qdrant ({_options.Collection})";

    /// <inheritdoc/>
    public override int VectorDimensions => _dimensions;

    /// <summary>
    /// Creates a new Qdrant vector store instance.
    /// </summary>
    /// <param name="options">Qdrant configuration options.</param>
    /// <param name="httpClient">Optional HTTP client for testing.</param>
    public QdrantVectorStore(QdrantOptions options, HttpClient? httpClient = null)
        : base(httpClient, options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string endpoint)
    {
        var request = new HttpRequestMessage(method, $"{_options.Host.TrimEnd('/')}{endpoint}");
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            request.Headers.Add("api-key", _options.ApiKey);
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
            var payload = new QdrantUpsertRequest
            {
                Points = new[]
                {
                    new QdrantPoint
                    {
                        Id = id,
                        Vector = vector,
                        Payload = metadata
                    }
                }
            };

            using var request = CreateRequest(HttpMethod.Put, $"/collections/{_options.Collection}/points");
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
                var points = batch.Select(r => new QdrantPoint
                {
                    Id = r.Id,
                    Vector = r.Vector,
                    Payload = r.Metadata
                }).ToArray();

                var payload = new QdrantUpsertRequest { Points = points };

                using var request = CreateRequest(HttpMethod.Put, $"/collections/{_options.Collection}/points");
                request.Content = JsonContent.Create(payload, options: JsonOptions);

                using var response = await HttpClient.SendAsync(request, token);
                response.EnsureSuccessStatusCode();

                Metrics.RecordUpsert(TimeSpan.Zero, points.Length - 1);
            }, "UpsertBatch", ct);
        }
    }

    /// <inheritdoc/>
    public override async Task<VectorRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        return await ExecuteWithResilienceAsync(async token =>
        {
            using var request = CreateRequest(HttpMethod.Get, $"/collections/{_options.Collection}/points/{id}");
            using var response = await HttpClient.SendAsync(request, token);

            if (!response.IsSuccessStatusCode)
                return null;

            var result = await response.Content.ReadFromJsonAsync<QdrantPointResponse>(JsonOptions, token);
            if (result?.Result != null)
            {
                return new VectorRecord(
                    id,
                    result.Result.Vector ?? Array.Empty<float>(),
                    result.Result.Payload);
            }
            return null;
        }, "Get", ct);
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ExecuteWithResilienceAsync(async token =>
        {
            var payload = new QdrantDeleteRequest
            {
                Points = new[] { id }
            };

            using var request = CreateRequest(HttpMethod.Post, $"/collections/{_options.Collection}/points/delete");
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
                var payload = new QdrantDeleteRequest { Points = batchIds };

                using var request = CreateRequest(HttpMethod.Post, $"/collections/{_options.Collection}/points/delete");
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
            var payload = new QdrantSearchRequest
            {
                Vector = query,
                Limit = topK,
                WithPayload = true,
                WithVector = _options.IncludeVectorsInSearch,
                ScoreThreshold = minScore > 0 ? minScore : null,
                Filter = filter != null ? BuildFilter(filter) : null
            };

            using var request = CreateRequest(HttpMethod.Post, $"/collections/{_options.Collection}/points/search");
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<QdrantSearchResponse>(JsonOptions, token);

            return result?.Result?
                .Select(m => new VectorSearchResult(
                    m.Id ?? "",
                    m.Score,
                    m.Vector,
                    m.Payload))
                .ToList() ?? new List<VectorSearchResult>();
        }, "Search", ct);
    }

    private static object? BuildFilter(Dictionary<string, object> filter)
    {
        // Build Qdrant filter format: { must: [{ key: "field", match: { value: x } }] }
        var conditions = filter.Select(kvp => new Dictionary<string, object>
        {
            ["key"] = kvp.Key,
            ["match"] = new Dictionary<string, object> { ["value"] = kvp.Value }
        }).ToList();

        return new Dictionary<string, object> { ["must"] = conditions };
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
            var distanceName = metric switch
            {
                DistanceMetric.Cosine => "Cosine",
                DistanceMetric.Euclidean => "Euclid",
                DistanceMetric.DotProduct => "Dot",
                DistanceMetric.Manhattan => "Manhattan",
                _ => "Cosine"
            };

            var payload = new
            {
                vectors = new
                {
                    size = dimensions,
                    distance = distanceName
                }
            };

            using var request = CreateRequest(HttpMethod.Put, $"/collections/{name}");
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            _dimensions = dimensions;
        }, "CreateCollection", ct);
    }

    /// <inheritdoc/>
    public override async Task DeleteCollectionAsync(string name, CancellationToken ct = default)
    {
        await ExecuteWithResilienceAsync(async token =>
        {
            using var request = CreateRequest(HttpMethod.Delete, $"/collections/{name}");
            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
        }, "DeleteCollection", ct);
    }

    /// <inheritdoc/>
    public override async Task<bool> CollectionExistsAsync(string name, CancellationToken ct = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, $"/collections/{name}");
            using var response = await HttpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public override async Task<VectorStoreStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        return await ExecuteWithResilienceAsync(async token =>
        {
            using var request = CreateRequest(HttpMethod.Get, $"/collections/{_options.Collection}");
            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<QdrantCollectionResponse>(JsonOptions, token);
            var info = result?.Result;

            _dimensions = info?.Config?.Params?.Vectors?.Size ?? 0;

            return new VectorStoreStatistics
            {
                TotalVectors = info?.PointsCount ?? 0,
                Dimensions = _dimensions,
                CollectionCount = 1,
                QueryCount = Metrics.QueryCount,
                AverageQueryLatencyMs = Metrics.AverageQueryLatencyMs,
                IndexType = "HNSW",
                Metric = DistanceMetric.Cosine,
                ExtendedStats = new Dictionary<string, object>
                {
                    ["segments_count"] = info?.SegmentsCount ?? 0,
                    ["indexed_vectors_count"] = info?.IndexedVectorsCount ?? 0,
                    ["status"] = info?.Status ?? "unknown"
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

            using var request = CreateRequest(HttpMethod.Get, "/");
            using var response = await HttpClient.SendAsync(request, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #region Request/Response Models

    private sealed class QdrantUpsertRequest
    {
        public QdrantPoint[] Points { get; set; } = Array.Empty<QdrantPoint>();
    }

    private sealed class QdrantPoint
    {
        public string Id { get; set; } = "";
        public float[] Vector { get; set; } = Array.Empty<float>();
        public Dictionary<string, object>? Payload { get; set; }
    }

    private sealed class QdrantDeleteRequest
    {
        public string[] Points { get; set; } = Array.Empty<string>();
    }

    private sealed class QdrantSearchRequest
    {
        public float[] Vector { get; set; } = Array.Empty<float>();
        public int Limit { get; set; }
        public bool WithPayload { get; set; }
        public bool WithVector { get; set; }
        public float? ScoreThreshold { get; set; }
        public object? Filter { get; set; }
    }

    private sealed class QdrantSearchResponse
    {
        public List<QdrantSearchResult>? Result { get; set; }
    }

    private sealed class QdrantSearchResult
    {
        public string? Id { get; set; }
        public float Score { get; set; }
        public float[]? Vector { get; set; }
        public Dictionary<string, object>? Payload { get; set; }
    }

    private sealed class QdrantPointResponse
    {
        public QdrantPointResult? Result { get; set; }
    }

    private sealed class QdrantPointResult
    {
        public float[]? Vector { get; set; }
        public Dictionary<string, object>? Payload { get; set; }
    }

    private sealed class QdrantCollectionResponse
    {
        public QdrantCollectionInfo? Result { get; set; }
    }

    private sealed class QdrantCollectionInfo
    {
        public long PointsCount { get; set; }
        public long IndexedVectorsCount { get; set; }
        public int SegmentsCount { get; set; }
        public string? Status { get; set; }
        public QdrantConfig? Config { get; set; }
    }

    private sealed class QdrantConfig
    {
        public QdrantParams? Params { get; set; }
    }

    private sealed class QdrantParams
    {
        public QdrantVectors? Vectors { get; set; }
    }

    private sealed class QdrantVectors
    {
        public int Size { get; set; }
        public string? Distance { get; set; }
    }

    #endregion
}
