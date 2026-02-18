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
/// Configuration for Chroma vector store.
/// </summary>
public sealed class ChromaOptions : VectorStoreOptions
{
    /// <summary>Chroma host URL (e.g., "http://localhost:8000").</summary>
    public string Host { get; init; } = "http://localhost:8000";

    /// <summary>Collection name.</summary>
    public string Collection { get; init; } = "datawarehouse";

    /// <summary>Tenant name for multi-tenancy.</summary>
    public string Tenant { get; init; } = "default_tenant";

    /// <summary>Database name.</summary>
    public string Database { get; init; } = "default_database";

    /// <summary>API key (if authentication is enabled).</summary>
    public string? ApiKey { get; init; }

    /// <summary>Whether to auto-create collection if it doesn't exist.</summary>
    public bool AutoCreateCollection { get; init; } = true;
}

/// <summary>
/// Production-ready ChromaDB vector database connector.
/// </summary>
/// <remarks>
/// Chroma is an AI-native open-source embedding database with a simple API.
/// Features:
/// <list type="bullet">
/// <item>Local persistent and HTTP client-server modes</item>
/// <item>Collections with automatic embedding (when using embedding function)</item>
/// <item>Where/where_document filtering with operators</item>
/// <item>Simple Python and JavaScript/TypeScript SDKs</item>
/// <item>Designed for development and production use</item>
/// </list>
/// </remarks>
public sealed class ChromaVectorStore : ProductionVectorStoreBase
{
    private readonly ChromaOptions _options;
    private string? _collectionId;
    private int _dimensions;

    /// <inheritdoc/>
    public override string StoreId => $"chroma-{_options.Collection}";

    /// <inheritdoc/>
    public override string DisplayName => $"Chroma ({_options.Collection})";

    /// <inheritdoc/>
    public override int VectorDimensions => _dimensions;

    /// <summary>
    /// Creates a new Chroma vector store instance.
    /// </summary>
    /// <param name="options">Chroma configuration options.</param>
    /// <param name="httpClient">Optional HTTP client for testing.</param>
    public ChromaVectorStore(ChromaOptions options, HttpClient? httpClient = null)
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
        request.Headers.Add("X-Chroma-Token", _options.ApiKey ?? "");
        return request;
    }

    private async Task EnsureCollectionAsync(CancellationToken ct)
    {
        if (_collectionId != null) return;

        // Try to get existing collection
        try
        {
            using var getRequest = CreateRequest(HttpMethod.Get,
                $"/api/v1/collections/{_options.Collection}?tenant={_options.Tenant}&database={_options.Database}");
            using var getResponse = await HttpClient.SendAsync(getRequest, ct);

            if (getResponse.IsSuccessStatusCode)
            {
                var collection = await getResponse.Content.ReadFromJsonAsync<ChromaCollection>(JsonOptions, ct);
                _collectionId = collection?.Id;
                return;
            }
        }
        catch { /* Collection fetch failure — may need to create */ }

        if (_options.AutoCreateCollection)
        {
            // Create collection
            var payload = new
            {
                name = _options.Collection,
                metadata = new { source = "datawarehouse" }
            };

            using var createRequest = CreateRequest(HttpMethod.Post,
                $"/api/v1/collections?tenant={_options.Tenant}&database={_options.Database}");
            createRequest.Content = JsonContent.Create(payload, options: JsonOptions);

            using var createResponse = await HttpClient.SendAsync(createRequest, ct);
            createResponse.EnsureSuccessStatusCode();

            var collection = await createResponse.Content.ReadFromJsonAsync<ChromaCollection>(JsonOptions, ct);
            _collectionId = collection?.Id;
        }
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
            await EnsureCollectionAsync(token);

            var payload = new ChromaUpsertRequest
            {
                Ids = new[] { id },
                Embeddings = new[] { vector },
                Metadatas = new[] { metadata }
            };

            using var request = CreateRequest(HttpMethod.Post, $"/api/v1/collections/{_collectionId}/upsert");
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
        }, "Upsert", ct);
    }

    /// <inheritdoc/>
    public override async Task UpsertBatchAsync(IEnumerable<VectorRecord> records, CancellationToken ct = default)
    {
        await EnsureCollectionAsync(ct);

        foreach (var batch in BatchItems(records, _options.MaxBatchSize))
        {
            await ExecuteWithResilienceAsync(async token =>
            {
                var list = batch.ToList();
                var payload = new ChromaUpsertRequest
                {
                    Ids = list.Select(r => r.Id).ToArray(),
                    Embeddings = list.Select(r => r.Vector).ToArray(),
                    Metadatas = list.Select(r => r.Metadata).ToArray()
                };

                using var request = CreateRequest(HttpMethod.Post, $"/api/v1/collections/{_collectionId}/upsert");
                request.Content = JsonContent.Create(payload, options: JsonOptions);

                using var response = await HttpClient.SendAsync(request, token);
                response.EnsureSuccessStatusCode();

                Metrics.RecordUpsert(TimeSpan.Zero, list.Count - 1);
            }, "UpsertBatch", ct);
        }
    }

    /// <inheritdoc/>
    public override async Task<VectorRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        return await ExecuteWithResilienceAsync(async token =>
        {
            await EnsureCollectionAsync(token);

            var payload = new ChromaGetRequest
            {
                Ids = new[] { id },
                Include = new[] { "embeddings", "metadatas" }
            };

            using var request = CreateRequest(HttpMethod.Post, $"/api/v1/collections/{_collectionId}/get");
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
                return null;

            var result = await response.Content.ReadFromJsonAsync<ChromaGetResponse>(JsonOptions, token);
            if (result?.Ids?.Length > 0)
            {
                return new VectorRecord(
                    id,
                    result.Embeddings?.FirstOrDefault() ?? Array.Empty<float>(),
                    result.Metadatas?.FirstOrDefault());
            }
            return null;
        }, "Get", ct);
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ExecuteWithResilienceAsync(async token =>
        {
            await EnsureCollectionAsync(token);

            var payload = new { ids = new[] { id } };

            using var request = CreateRequest(HttpMethod.Post, $"/api/v1/collections/{_collectionId}/delete");
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            Metrics.RecordDelete();
        }, "Delete", ct);
    }

    /// <inheritdoc/>
    public override async Task DeleteBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        await EnsureCollectionAsync(ct);

        var idList = ids.ToList();
        foreach (var batch in BatchItems(idList, _options.MaxBatchSize))
        {
            await ExecuteWithResilienceAsync(async token =>
            {
                var batchIds = batch.ToArray();
                var payload = new { ids = batchIds };

                using var request = CreateRequest(HttpMethod.Post, $"/api/v1/collections/{_collectionId}/delete");
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
            await EnsureCollectionAsync(token);

            var include = new List<string> { "metadatas", "distances" };
            if (_options.IncludeVectorsInSearch)
            {
                include.Add("embeddings");
            }

            var payload = new ChromaQueryRequest
            {
                QueryEmbeddings = new[] { query },
                NResults = topK,
                Include = include.ToArray(),
                Where = filter != null ? BuildWhereFilter(filter) : null
            };

            using var request = CreateRequest(HttpMethod.Post, $"/api/v1/collections/{_collectionId}/query");
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ChromaQueryResponse>(JsonOptions, token);
            var results = new List<VectorSearchResult>();

            if (result?.Ids?.Length > 0)
            {
                var ids = result.Ids[0];
                var embeddings = result.Embeddings?[0];
                var distances = result.Distances?[0];
                var metadatas = result.Metadatas?[0];

                for (int i = 0; i < ids.Length; i++)
                {
                    // Chroma returns distances (L2), convert to similarity score
                    var distance = distances?[i] ?? 0f;
                    var score = 1.0f / (1.0f + distance); // Convert distance to similarity

                    if (score >= minScore)
                    {
                        results.Add(new VectorSearchResult(
                            ids[i],
                            score,
                            embeddings?[i],
                            metadatas?[i]));
                    }
                }
            }

            return results;
        }, "Search", ct);
    }

    private static Dictionary<string, object>? BuildWhereFilter(Dictionary<string, object> filter)
    {
        // Chroma where format: { "field": { "$eq": value } }
        // For multiple conditions: { "$and": [{ "field1": value1 }, { "field2": value2 }] }
        if (filter.Count == 0) return null;

        if (filter.Count == 1)
        {
            var kvp = filter.First();
            return new Dictionary<string, object>
            {
                [kvp.Key] = new Dictionary<string, object> { ["$eq"] = kvp.Value }
            };
        }

        var conditions = filter.Select(kvp => new Dictionary<string, object>
        {
            [kvp.Key] = new Dictionary<string, object> { ["$eq"] = kvp.Value }
        }).Cast<object>().ToList();

        return new Dictionary<string, object> { ["$and"] = conditions };
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
            var distanceFn = metric switch
            {
                DistanceMetric.Cosine => "cosine",
                DistanceMetric.Euclidean => "l2",
                DistanceMetric.DotProduct => "ip",
                _ => "cosine"
            };

            var payload = new
            {
                name,
                metadata = new
                {
                    source = "datawarehouse",
                    dimensions,
                    hnsw_space = distanceFn
                }
            };

            using var request = CreateRequest(HttpMethod.Post,
                $"/api/v1/collections?tenant={_options.Tenant}&database={_options.Database}");
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
            using var request = CreateRequest(HttpMethod.Delete,
                $"/api/v1/collections/{name}?tenant={_options.Tenant}&database={_options.Database}");

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            if (name == _options.Collection)
            {
                _collectionId = null;
            }
        }, "DeleteCollection", ct);
    }

    /// <inheritdoc/>
    public override async Task<bool> CollectionExistsAsync(string name, CancellationToken ct = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get,
                $"/api/v1/collections/{name}?tenant={_options.Tenant}&database={_options.Database}");
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
            await EnsureCollectionAsync(token);

            using var request = CreateRequest(HttpMethod.Get, $"/api/v1/collections/{_collectionId}/count");
            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var count = await response.Content.ReadFromJsonAsync<long>(JsonOptions, token);

            return new VectorStoreStatistics
            {
                TotalVectors = count,
                Dimensions = _dimensions,
                CollectionCount = 1,
                QueryCount = Metrics.QueryCount,
                AverageQueryLatencyMs = Metrics.AverageQueryLatencyMs,
                IndexType = "HNSW",
                Metric = DistanceMetric.Cosine,
                ExtendedStats = new Dictionary<string, object>
                {
                    ["collectionId"] = _collectionId ?? "",
                    ["tenant"] = _options.Tenant,
                    ["database"] = _options.Database
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

            using var request = CreateRequest(HttpMethod.Get, "/api/v1/heartbeat");
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

    private sealed class ChromaCollection
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    private sealed class ChromaUpsertRequest
    {
        public string[] Ids { get; set; } = Array.Empty<string>();
        public float[][] Embeddings { get; set; } = Array.Empty<float[]>();
        public Dictionary<string, object>?[] Metadatas { get; set; } = Array.Empty<Dictionary<string, object>?>();
    }

    private sealed class ChromaGetRequest
    {
        public string[] Ids { get; set; } = Array.Empty<string>();
        public string[] Include { get; set; } = Array.Empty<string>();
    }

    private sealed class ChromaGetResponse
    {
        public string[]? Ids { get; set; }
        public float[][]? Embeddings { get; set; }
        public Dictionary<string, object>?[]? Metadatas { get; set; }
    }

    private sealed class ChromaQueryRequest
    {
        public float[][] QueryEmbeddings { get; set; } = Array.Empty<float[]>();
        public int NResults { get; set; }
        public string[] Include { get; set; } = Array.Empty<string>();
        public Dictionary<string, object>? Where { get; set; }
    }

    private sealed class ChromaQueryResponse
    {
        public string[][]? Ids { get; set; }
        public float[][][]? Embeddings { get; set; }
        public float[][]? Distances { get; set; }
        public Dictionary<string, object>?[][]? Metadatas { get; set; }
    }

    #endregion
}
