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

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.VectorStores;

/// <summary>
/// kNN search type for Elasticsearch.
/// </summary>
public enum ElasticsearchKnnType
{
    /// <summary>Approximate kNN using HNSW.</summary>
    Approximate,

    /// <summary>Exact kNN using script scoring (slower but precise).</summary>
    Exact
}

/// <summary>
/// Configuration for Elasticsearch vector store.
/// </summary>
public sealed class ElasticsearchOptions : VectorStoreOptions
{
    /// <summary>Elasticsearch host URL (e.g., "http://localhost:9200").</summary>
    public string Host { get; init; } = "http://localhost:9200";

    /// <summary>Index name.</summary>
    public string IndexName { get; init; } = "datawarehouse-vectors";

    /// <summary>Username for authentication.</summary>
    public string? Username { get; init; }

    /// <summary>Password for authentication.</summary>
    public string? Password { get; init; }

    /// <summary>API key for authentication.</summary>
    public string? ApiKey { get; init; }

    /// <summary>kNN search type.</summary>
    public ElasticsearchKnnType KnnType { get; init; } = ElasticsearchKnnType.Approximate;

    /// <summary>Number of candidates to consider during kNN search.</summary>
    public int NumCandidates { get; init; } = 100;

    /// <summary>HNSW M parameter.</summary>
    public int HnswM { get; init; } = 16;

    /// <summary>HNSW ef_construction parameter.</summary>
    public int HnswEfConstruction { get; init; } = 100;

    /// <summary>Number of shards for the index.</summary>
    public int NumberOfShards { get; init; } = 1;

    /// <summary>Number of replicas for the index.</summary>
    public int NumberOfReplicas { get; init; } = 0;
}

/// <summary>
/// Production-ready Elasticsearch/OpenSearch kNN vector store connector.
/// </summary>
/// <remarks>
/// Elasticsearch (and OpenSearch) support vector search via dense_vector field type.
/// Features:
/// <list type="bullet">
/// <item>dense_vector field type with configurable dimensions</item>
/// <item>Approximate kNN using HNSW (7.x+)</item>
/// <item>Exact kNN using script scoring</item>
/// <item>Hybrid search combining kNN with traditional queries</item>
/// <item>Scalable distributed architecture</item>
/// </list>
/// </remarks>
public sealed class ElasticsearchVectorStore : ProductionVectorStoreBase
{
    private readonly ElasticsearchOptions _options;
    private int _dimensions;

    /// <inheritdoc/>
    public override string StoreId => $"elasticsearch-{_options.IndexName}";

    /// <inheritdoc/>
    public override string DisplayName => $"Elasticsearch ({_options.IndexName})";

    /// <inheritdoc/>
    public override int VectorDimensions => _dimensions;

    /// <summary>
    /// Creates a new Elasticsearch vector store instance.
    /// </summary>
    /// <param name="options">Elasticsearch configuration options.</param>
    /// <param name="httpClient">Optional HTTP client for testing.</param>
    public ElasticsearchVectorStore(ElasticsearchOptions options, HttpClient? httpClient = null)
        : base(httpClient, options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string endpoint)
    {
        var request = new HttpRequestMessage(method, $"{_options.Host.TrimEnd('/')}{endpoint}");

        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            request.Headers.Add("Authorization", $"ApiKey {_options.ApiKey}");
        }
        else if (!string.IsNullOrEmpty(_options.Username) && !string.IsNullOrEmpty(_options.Password))
        {
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}"));
            request.Headers.Add("Authorization", $"Basic {auth}");
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
            var doc = new Dictionary<string, object>
            {
                ["vector"] = vector,
                ["id"] = id
            };

            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    doc[kvp.Key] = kvp.Value;
                }
            }

            using var request = CreateRequest(HttpMethod.Put, $"/{_options.IndexName}/_doc/{Uri.EscapeDataString(id)}");
            request.Content = JsonContent.Create(doc, options: JsonOptions);

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
                // Build NDJSON bulk request
                var sb = new StringBuilder();
                var count = 0;

                foreach (var record in batch)
                {
                    // Action line
                    sb.AppendLine(JsonSerializer.Serialize(new
                    {
                        index = new { _index = _options.IndexName, _id = record.Id }
                    }, JsonOptions));

                    // Document line
                    var doc = new Dictionary<string, object>
                    {
                        ["vector"] = record.Vector,
                        ["id"] = record.Id
                    };

                    if (record.Metadata != null)
                    {
                        foreach (var kvp in record.Metadata)
                        {
                            doc[kvp.Key] = kvp.Value;
                        }
                    }

                    sb.AppendLine(JsonSerializer.Serialize(doc, JsonOptions));
                    count++;
                }

                using var request = CreateRequest(HttpMethod.Post, "/_bulk");
                request.Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/x-ndjson");

                using var response = await HttpClient.SendAsync(request, token);
                response.EnsureSuccessStatusCode();

                Metrics.RecordUpsert(TimeSpan.Zero, count - 1);
            }, "UpsertBatch", ct);
        }
    }

    /// <inheritdoc/>
    public override async Task<VectorRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        return await ExecuteWithResilienceAsync(async token =>
        {
            using var request = CreateRequest(HttpMethod.Get, $"/{_options.IndexName}/_doc/{Uri.EscapeDataString(id)}");
            using var response = await HttpClient.SendAsync(request, token);

            if (!response.IsSuccessStatusCode)
                return null;

            var result = await response.Content.ReadFromJsonAsync<ElasticsearchGetResponse>(JsonOptions, token);

            if (result?.Found == true && result.Source != null)
            {
                var vector = result.Source.TryGetValue("vector", out var v) && v is JsonElement je
                    ? je.Deserialize<float[]>()
                    : null;

                var metadata = result.Source
                    .Where(kvp => kvp.Key != "vector" && kvp.Key != "id")
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

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
            using var request = CreateRequest(HttpMethod.Delete, $"/{_options.IndexName}/_doc/{Uri.EscapeDataString(id)}");
            using var response = await HttpClient.SendAsync(request, token);
            // 404 is acceptable for delete
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
            }

            Metrics.RecordDelete();
        }, "Delete", ct);
    }

    /// <inheritdoc/>
    public override async Task DeleteBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;

        await ExecuteWithResilienceAsync(async token =>
        {
            // Build NDJSON bulk delete request
            var sb = new StringBuilder();

            foreach (var id in idList)
            {
                sb.AppendLine(JsonSerializer.Serialize(new
                {
                    delete = new { _index = _options.IndexName, _id = id }
                }, JsonOptions));
            }

            using var request = CreateRequest(HttpMethod.Post, "/_bulk");
            request.Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/x-ndjson");

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            Metrics.RecordDelete(idList.Count);
        }, "DeleteBatch", ct);
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
            object searchBody;

            if (_options.KnnType == ElasticsearchKnnType.Approximate)
            {
                // Approximate kNN search (Elasticsearch 8.x style)
                searchBody = new
                {
                    knn = new
                    {
                        field = "vector",
                        query_vector = query,
                        k = topK,
                        num_candidates = _options.NumCandidates,
                        filter = filter != null ? BuildFilter(filter) : null
                    },
                    _source = _options.IncludeVectorsInSearch
                        ? new[] { "*" }
                        : new[] { "id", "metadata" },
                    min_score = minScore > 0 ? minScore : (float?)null
                };
            }
            else
            {
                // Exact kNN using script_score
                searchBody = new
                {
                    query = new
                    {
                        script_score = new
                        {
                            query = filter != null ? (object)new { @bool = new { filter = BuildFilterArray(filter) } } : new { match_all = new { } },
                            script = new
                            {
                                source = "cosineSimilarity(params.query_vector, 'vector') + 1.0",
                                @params = new { query_vector = query }
                            }
                        }
                    },
                    size = topK,
                    min_score = minScore > 0 ? minScore + 1.0f : (float?)null
                };
            }

            using var request = CreateRequest(HttpMethod.Post, $"/{_options.IndexName}/_search");
            request.Content = JsonContent.Create(searchBody, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ElasticsearchSearchResponse>(JsonOptions, token);

            return result?.Hits?.Hits?
                .Select(hit =>
                {
                    var score = _options.KnnType == ElasticsearchKnnType.Exact
                        ? hit.Score - 1.0f  // Undo the +1.0 offset
                        : hit.Score;

                    float[]? vector = null;
                    if (_options.IncludeVectorsInSearch && hit.Source?.TryGetValue("vector", out var v) == true && v is JsonElement je)
                    {
                        vector = je.Deserialize<float[]>();
                    }

                    var metadata = hit.Source?
                        .Where(kvp => kvp.Key != "vector" && kvp.Key != "id")
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                    return new VectorSearchResult(hit.Id ?? "", score, vector, metadata);
                })
                .ToList() ?? new List<VectorSearchResult>();
        }, "Search", ct);
    }

    private static object BuildFilter(Dictionary<string, object> filter)
    {
        var terms = filter.Select(kvp => new Dictionary<string, object>
        {
            ["term"] = new Dictionary<string, object> { [kvp.Key] = kvp.Value }
        }).ToList();

        return new { @bool = new { filter = terms } };
    }

    private static object[] BuildFilterArray(Dictionary<string, object> filter)
    {
        return filter.Select(kvp => (object)new Dictionary<string, object>
        {
            ["term"] = new Dictionary<string, object> { [kvp.Key] = kvp.Value }
        }).ToArray();
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
            var similarity = metric switch
            {
                DistanceMetric.Cosine => "cosine",
                DistanceMetric.Euclidean => "l2_norm",
                DistanceMetric.DotProduct => "dot_product",
                _ => "cosine"
            };

            var mapping = new
            {
                settings = new
                {
                    number_of_shards = _options.NumberOfShards,
                    number_of_replicas = _options.NumberOfReplicas
                },
                mappings = new
                {
                    properties = new
                    {
                        id = new { type = "keyword" },
                        vector = new
                        {
                            type = "dense_vector",
                            dims = dimensions,
                            index = true,
                            similarity,
                            index_options = new
                            {
                                type = "hnsw",
                                m = _options.HnswM,
                                ef_construction = _options.HnswEfConstruction
                            }
                        }
                    }
                }
            };

            using var request = CreateRequest(HttpMethod.Put, $"/{name}");
            request.Content = JsonContent.Create(mapping, options: JsonOptions);

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
            using var request = CreateRequest(HttpMethod.Delete, $"/{name}");
            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
        }, "DeleteCollection", ct);
    }

    /// <inheritdoc/>
    public override async Task<bool> CollectionExistsAsync(string name, CancellationToken ct = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Head, $"/{name}");
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
            using var request = CreateRequest(HttpMethod.Get, $"/{_options.IndexName}/_stats");
            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ElasticsearchStatsResponse>(JsonOptions, token);
            var indexStats = result?.Indices?.GetValueOrDefault(_options.IndexName);

            return new VectorStoreStatistics
            {
                TotalVectors = indexStats?.Primaries?.Docs?.Count ?? 0,
                Dimensions = _dimensions,
                StorageSizeBytes = indexStats?.Primaries?.Store?.SizeInBytes,
                CollectionCount = 1,
                QueryCount = Metrics.QueryCount,
                AverageQueryLatencyMs = Metrics.AverageQueryLatencyMs,
                IndexType = "HNSW",
                Metric = DistanceMetric.Cosine,
                ExtendedStats = new Dictionary<string, object>
                {
                    ["indexName"] = _options.IndexName,
                    ["knnType"] = _options.KnnType.ToString(),
                    ["numberOfShards"] = _options.NumberOfShards
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

            using var request = CreateRequest(HttpMethod.Get, "/_cluster/health");
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

    #region Response Models

    private sealed class ElasticsearchGetResponse
    {
        public bool Found { get; set; }
        [JsonPropertyName("_source")]
        public Dictionary<string, object>? Source { get; set; }
    }

    private sealed class ElasticsearchSearchResponse
    {
        public ElasticsearchHitsContainer? Hits { get; set; }
    }

    private sealed class ElasticsearchHitsContainer
    {
        public List<ElasticsearchHit>? Hits { get; set; }
    }

    private sealed class ElasticsearchHit
    {
        [JsonPropertyName("_id")]
        public string? Id { get; set; }

        [JsonPropertyName("_score")]
        public float Score { get; set; }

        [JsonPropertyName("_source")]
        public Dictionary<string, object>? Source { get; set; }
    }

    private sealed class ElasticsearchStatsResponse
    {
        public Dictionary<string, ElasticsearchIndexStats>? Indices { get; set; }
    }

    private sealed class ElasticsearchIndexStats
    {
        public ElasticsearchPrimaries? Primaries { get; set; }
    }

    private sealed class ElasticsearchPrimaries
    {
        public ElasticsearchDocs? Docs { get; set; }
        public ElasticsearchStore? Store { get; set; }
    }

    private sealed class ElasticsearchDocs
    {
        public long Count { get; set; }
    }

    private sealed class ElasticsearchStore
    {
        public long SizeInBytes { get; set; }
    }

    #endregion
}
