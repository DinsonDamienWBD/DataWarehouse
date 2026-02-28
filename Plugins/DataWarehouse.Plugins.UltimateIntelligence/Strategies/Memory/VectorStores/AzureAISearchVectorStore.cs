using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.VectorStores;

/// <summary>
/// Vector search algorithm for Azure AI Search.
/// </summary>
public enum AzureSearchAlgorithm
{
    /// <summary>HNSW algorithm (recommended for most use cases).</summary>
    Hnsw,

    /// <summary>Exhaustive KNN (exact but slower).</summary>
    ExhaustiveKnn
}

/// <summary>
/// Configuration for Azure AI Search vector store.
/// </summary>
public sealed class AzureAISearchOptions : VectorStoreOptions
{
    /// <summary>Azure AI Search service endpoint (e.g., "https://myservice.search.windows.net").</summary>
    public required string Endpoint { get; init; }

    /// <summary>Azure AI Search admin or query API key.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Index name.</summary>
    public string IndexName { get; init; } = "datawarehouse-vectors";

    /// <summary>API version to use.</summary>
    public string ApiVersion { get; init; } = "2024-07-01";

    /// <summary>Vector search algorithm.</summary>
    public AzureSearchAlgorithm Algorithm { get; init; } = AzureSearchAlgorithm.Hnsw;

    /// <summary>HNSW M parameter.</summary>
    public int HnswM { get; init; } = 4;

    /// <summary>HNSW efConstruction parameter.</summary>
    public int HnswEfConstruction { get; init; } = 400;

    /// <summary>HNSW efSearch parameter.</summary>
    public int HnswEfSearch { get; init; } = 500;

    /// <summary>Whether to use semantic ranking (hybrid search).</summary>
    public bool UseSemanticRanking { get; init; } = false;

    /// <summary>Semantic configuration name.</summary>
    public string? SemanticConfigurationName { get; init; }
}

/// <summary>
/// Production-ready Azure AI Search (formerly Cognitive Search) vector store connector.
/// </summary>
/// <remarks>
/// Azure AI Search provides enterprise-grade vector search capabilities.
/// Features:
/// <list type="bullet">
/// <item>Vector search indexes with HNSW or exhaustive KNN</item>
/// <item>Hybrid search combining vectors with keyword search</item>
/// <item>Semantic ranking for improved relevance</item>
/// <item>Integrated vectorization with Azure OpenAI</item>
/// <item>Enterprise security and compliance</item>
/// </list>
/// </remarks>
public sealed class AzureAISearchVectorStore : ProductionVectorStoreBase
{
    private readonly AzureAISearchOptions _options;
    private int _dimensions;

    /// <inheritdoc/>
    public override string StoreId => $"azure-ai-search-{_options.IndexName}";

    /// <inheritdoc/>
    public override string DisplayName => $"Azure AI Search ({_options.IndexName})";

    /// <inheritdoc/>
    public override int VectorDimensions => _dimensions;

    /// <summary>
    /// Creates a new Azure AI Search vector store instance.
    /// </summary>
    /// <param name="options">Azure AI Search configuration options.</param>
    /// <param name="httpClient">Optional HTTP client for testing.</param>
    public AzureAISearchVectorStore(AzureAISearchOptions options, HttpClient? httpClient = null)
        : base(httpClient, options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // Finding 3224: Validate endpoint and API key on construction.
        if (string.IsNullOrWhiteSpace(options.Endpoint))
            throw new ArgumentException("Azure AI Search endpoint URL is required", nameof(options));
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out _))
            throw new ArgumentException($"Azure AI Search endpoint is not a valid absolute URI: '{options.Endpoint}'", nameof(options));
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("API key is required", nameof(options));
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string endpoint)
    {
        var url = $"{_options.Endpoint.TrimEnd('/')}{endpoint}";
        if (!url.Contains('?'))
            url += $"?api-version={_options.ApiVersion}";
        else
            url += $"&api-version={_options.ApiVersion}";

        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("api-key", _options.ApiKey);
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
            var doc = new Dictionary<string, object>
            {
                ["@search.action"] = "mergeOrUpload",
                ["id"] = id,
                ["vector"] = vector
            };

            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    doc[kvp.Key] = kvp.Value;
                }
            }

            var payload = new { value = new[] { doc } };

            using var request = CreateRequest(HttpMethod.Post, $"/indexes/{_options.IndexName}/docs/index");
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
                var docs = batch.Select(r =>
                {
                    var doc = new Dictionary<string, object>
                    {
                        ["@search.action"] = "mergeOrUpload",
                        ["id"] = r.Id,
                        ["vector"] = r.Vector
                    };

                    if (r.Metadata != null)
                    {
                        foreach (var kvp in r.Metadata)
                        {
                            doc[kvp.Key] = kvp.Value;
                        }
                    }

                    return doc;
                }).ToArray();

                var payload = new { value = docs };

                using var request = CreateRequest(HttpMethod.Post, $"/indexes/{_options.IndexName}/docs/index");
                request.Content = JsonContent.Create(payload, options: JsonOptions);

                using var response = await HttpClient.SendAsync(request, token);
                response.EnsureSuccessStatusCode();

                Metrics.RecordUpsert(TimeSpan.Zero, docs.Length - 1);
            }, "UpsertBatch", ct);
        }
    }

    /// <inheritdoc/>
    public override async Task<VectorRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        return await ExecuteWithResilienceAsync(async token =>
        {
            var encodedId = Uri.EscapeDataString(id);
            using var request = CreateRequest(HttpMethod.Get, $"/indexes/{_options.IndexName}/docs/{encodedId}");
            using var response = await HttpClient.SendAsync(request, token);

            if (!response.IsSuccessStatusCode)
                return null;

            var doc = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>(JsonOptions, token);

            if (doc != null)
            {
                var vector = doc.TryGetValue("vector", out var v)
                    ? v.Deserialize<float[]>()
                    : null;

                var metadata = doc
                    .Where(kvp => kvp.Key != "vector" && kvp.Key != "id" && !kvp.Key.StartsWith("@"))
                    .ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value.ToString()!);

                return new VectorRecord(id, vector ?? Array.Empty<float>(), metadata);
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
                value = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["@search.action"] = "delete",
                        ["id"] = id
                    }
                }
            };

            using var request = CreateRequest(HttpMethod.Post, $"/indexes/{_options.IndexName}/docs/index");
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
                var docs = batch.Select(id => new Dictionary<string, object>
                {
                    ["@search.action"] = "delete",
                    ["id"] = id
                }).ToArray();

                var payload = new { value = docs };

                using var request = CreateRequest(HttpMethod.Post, $"/indexes/{_options.IndexName}/docs/index");
                request.Content = JsonContent.Create(payload, options: JsonOptions);

                using var response = await HttpClient.SendAsync(request, token);
                response.EnsureSuccessStatusCode();

                Metrics.RecordDelete(docs.Length);
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
            var vectorQuery = new
            {
                kind = "vector",
                vector = query,
                k = topK,
                fields = "vector"
            };

            var searchPayload = new Dictionary<string, object>
            {
                ["count"] = true,
                ["vectorQueries"] = new[] { vectorQuery },
                ["select"] = _options.IncludeVectorsInSearch ? "*" : "id"
            };

            if (filter != null)
            {
                searchPayload["filter"] = BuildFilter(filter);
            }

            if (_options.UseSemanticRanking && !string.IsNullOrEmpty(_options.SemanticConfigurationName))
            {
                searchPayload["queryType"] = "semantic";
                searchPayload["semanticConfiguration"] = _options.SemanticConfigurationName;
            }

            using var request = CreateRequest(HttpMethod.Post, $"/indexes/{_options.IndexName}/docs/search");
            request.Content = JsonContent.Create(searchPayload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<AzureSearchResponse>(JsonOptions, token);

            return result?.Value?
                .Where(hit => hit.SearchScore >= minScore)
                .Select(hit =>
                {
                    float[]? vector = null;
                    if (_options.IncludeVectorsInSearch && hit.Document?.TryGetValue("vector", out var v) == true && v is JsonElement je)
                    {
                        vector = je.Deserialize<float[]>();
                    }

                    var metadata = hit.Document?
                        .Where(kvp => kvp.Key != "vector" && kvp.Key != "id" && !kvp.Key.StartsWith("@"))
                        .ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value.ToString()!);

                    var id = hit.Document?.TryGetValue("id", out var idVal) == true
                        ? idVal.ToString() ?? ""
                        : "";

                    return new VectorSearchResult(id, hit.SearchScore, vector, metadata);
                })
                .ToList() ?? new List<VectorSearchResult>();
        }, "Search", ct);
    }

    private static string BuildFilter(Dictionary<string, object> filter)
    {
        var conditions = filter.Select(kvp =>
        {
            if (kvp.Value is string s)
                return $"{kvp.Key} eq '{s}'";
            if (kvp.Value is bool b)
                return $"{kvp.Key} eq {b.ToString().ToLower()}";
            return $"{kvp.Key} eq {kvp.Value}";
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
            var algorithmConfig = _options.Algorithm == AzureSearchAlgorithm.Hnsw
                ? (object)new
                {
                    name = "hnsw-config",
                    kind = "hnsw",
                    hnswParameters = new
                    {
                        m = _options.HnswM,
                        efConstruction = _options.HnswEfConstruction,
                        efSearch = _options.HnswEfSearch,
                        metric = metric switch
                        {
                            DistanceMetric.Cosine => "cosine",
                            DistanceMetric.Euclidean => "euclidean",
                            DistanceMetric.DotProduct => "dotProduct",
                            _ => "cosine"
                        }
                    }
                }
                : (object)new
                {
                    name = "exhaustive-knn-config",
                    kind = "exhaustiveKnn",
                    exhaustiveKnnParameters = new
                    {
                        metric = metric switch
                        {
                            DistanceMetric.Cosine => "cosine",
                            DistanceMetric.Euclidean => "euclidean",
                            DistanceMetric.DotProduct => "dotProduct",
                            _ => "cosine"
                        }
                    }
                };

            var indexDef = new
            {
                name,
                fields = new object[]
                {
                    new
                    {
                        name = "id",
                        type = "Edm.String",
                        key = true,
                        searchable = false,
                        filterable = true,
                        sortable = false,
                        facetable = false,
                        retrievable = true
                    },
                    new
                    {
                        name = "vector",
                        type = $"Collection(Edm.Single)",
                        searchable = true,
                        filterable = false,
                        sortable = false,
                        facetable = false,
                        retrievable = true,
                        dimensions,
                        vectorSearchProfile = "vector-profile"
                    }
                },
                vectorSearch = new
                {
                    algorithms = new[] { algorithmConfig },
                    profiles = new[]
                    {
                        new
                        {
                            name = "vector-profile",
                            algorithmConfigurationName = _options.Algorithm == AzureSearchAlgorithm.Hnsw
                                ? "hnsw-config"
                                : "exhaustive-knn-config"
                        }
                    }
                }
            };

            using var request = CreateRequest(HttpMethod.Put, $"/indexes/{name}");
            request.Content = JsonContent.Create(indexDef, options: JsonOptions);

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
            using var request = CreateRequest(HttpMethod.Delete, $"/indexes/{name}");
            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
        }, "DeleteCollection", ct);
    }

    /// <inheritdoc/>
    public override async Task<bool> CollectionExistsAsync(string name, CancellationToken ct = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, $"/indexes/{name}");
            using var response = await HttpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            Debug.WriteLine($"Caught exception in AzureAISearchVectorStore.cs");
            return false;
        }
    }

    /// <inheritdoc/>
    public override async Task<VectorStoreStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        return await ExecuteWithResilienceAsync(async token =>
        {
            using var request = CreateRequest(HttpMethod.Get, $"/indexes/{_options.IndexName}/stats");
            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<AzureIndexStats>(JsonOptions, token);

            return new VectorStoreStatistics
            {
                TotalVectors = result?.DocumentCount ?? 0,
                Dimensions = _dimensions,
                StorageSizeBytes = result?.StorageSize,
                CollectionCount = 1,
                QueryCount = Metrics.QueryCount,
                AverageQueryLatencyMs = Metrics.AverageQueryLatencyMs,
                IndexType = _options.Algorithm.ToString().ToUpperInvariant(),
                Metric = DistanceMetric.Cosine,
                ExtendedStats = new Dictionary<string, object>
                {
                    ["indexName"] = _options.IndexName,
                    ["algorithm"] = _options.Algorithm.ToString(),
                    ["useSemanticRanking"] = _options.UseSemanticRanking
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

            using var request = CreateRequest(HttpMethod.Get, "/servicestats");
            using var response = await HttpClient.SendAsync(request, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            Debug.WriteLine($"Caught exception in AzureAISearchVectorStore.cs");
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #region Response Models

    private sealed class AzureSearchResponse
    {
        [JsonPropertyName("@odata.count")]
        public long? Count { get; set; }

        public List<AzureSearchHit>? Value { get; set; }
    }

    private sealed class AzureSearchHit
    {
        [JsonPropertyName("@search.score")]
        public float SearchScore { get; set; }

        [JsonExtensionData]
        public Dictionary<string, object>? Document { get; set; }
    }

    private sealed class AzureIndexStats
    {
        public long DocumentCount { get; set; }
        public long StorageSize { get; set; }
    }

    #endregion
}
