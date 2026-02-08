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
/// Configuration for Weaviate vector store.
/// </summary>
public sealed class WeaviateOptions : VectorStoreOptions
{
    /// <summary>Weaviate host URL (e.g., "http://localhost:8080" or "https://xxx.weaviate.network").</summary>
    public string Host { get; init; } = "http://localhost:8080";

    /// <summary>Class name for storing objects (Weaviate uses classes, not collections).</summary>
    public string ClassName { get; init; } = "DataWarehouse";

    /// <summary>API key for Weaviate Cloud.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Tenant name for multi-tenancy.</summary>
    public string? Tenant { get; init; }

    /// <summary>Whether to enable hybrid search (vector + BM25).</summary>
    public bool EnableHybridSearch { get; init; } = false;

    /// <summary>Alpha parameter for hybrid search (0 = pure BM25, 1 = pure vector).</summary>
    public float HybridAlpha { get; init; } = 0.5f;
}

/// <summary>
/// Production-ready Weaviate vector database connector.
/// </summary>
/// <remarks>
/// Weaviate is an open-source vector database with GraphQL interface.
/// Features:
/// <list type="bullet">
/// <item>GraphQL and REST APIs</item>
/// <item>Schema/class management with automatic vectorization</item>
/// <item>Hybrid search combining vector and BM25</item>
/// <item>Multi-tenancy support</item>
/// <item>Built-in modules for text/image vectorization</item>
/// </list>
/// </remarks>
public sealed class WeaviateVectorStore : ProductionVectorStoreBase
{
    private readonly WeaviateOptions _options;
    private int _dimensions;

    /// <inheritdoc/>
    public override string StoreId => $"weaviate-{_options.ClassName}";

    /// <inheritdoc/>
    public override string DisplayName => $"Weaviate ({_options.ClassName})";

    /// <inheritdoc/>
    public override int VectorDimensions => _dimensions;

    /// <summary>
    /// Creates a new Weaviate vector store instance.
    /// </summary>
    /// <param name="options">Weaviate configuration options.</param>
    /// <param name="httpClient">Optional HTTP client for testing.</param>
    public WeaviateVectorStore(WeaviateOptions options, HttpClient? httpClient = null)
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
            var payload = new WeaviateObject
            {
                Class = _options.ClassName,
                Id = id,
                Vector = vector,
                Properties = metadata ?? new Dictionary<string, object>()
            };

            if (!string.IsNullOrEmpty(_options.Tenant))
            {
                payload.Tenant = _options.Tenant;
            }

            using var request = CreateRequest(HttpMethod.Post, "/v1/objects");
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);

            // Handle update case (object already exists)
            if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            {
                using var updateRequest = CreateRequest(HttpMethod.Put, $"/v1/objects/{_options.ClassName}/{id}");
                updateRequest.Content = JsonContent.Create(payload, options: JsonOptions);
                using var updateResponse = await HttpClient.SendAsync(updateRequest, token);
                updateResponse.EnsureSuccessStatusCode();
            }
            else
            {
                response.EnsureSuccessStatusCode();
            }
        }, "Upsert", ct);
    }

    /// <inheritdoc/>
    public override async Task UpsertBatchAsync(IEnumerable<VectorRecord> records, CancellationToken ct = default)
    {
        foreach (var batch in BatchItems(records, _options.MaxBatchSize))
        {
            await ExecuteWithResilienceAsync(async token =>
            {
                var objects = batch.Select(r => new WeaviateObject
                {
                    Class = _options.ClassName,
                    Id = r.Id,
                    Vector = r.Vector,
                    Properties = r.Metadata ?? new Dictionary<string, object>(),
                    Tenant = _options.Tenant
                }).ToArray();

                var payload = new { objects };

                using var request = CreateRequest(HttpMethod.Post, "/v1/batch/objects");
                request.Content = JsonContent.Create(payload, options: JsonOptions);

                using var response = await HttpClient.SendAsync(request, token);
                response.EnsureSuccessStatusCode();

                Metrics.RecordUpsert(TimeSpan.Zero, objects.Length - 1);
            }, "UpsertBatch", ct);
        }
    }

    /// <inheritdoc/>
    public override async Task<VectorRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        return await ExecuteWithResilienceAsync(async token =>
        {
            var url = $"/v1/objects/{_options.ClassName}/{id}?include=vector";
            if (!string.IsNullOrEmpty(_options.Tenant))
            {
                url += $"&tenant={Uri.EscapeDataString(_options.Tenant)}";
            }

            using var request = CreateRequest(HttpMethod.Get, url);
            using var response = await HttpClient.SendAsync(request, token);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<WeaviateObject>(JsonOptions, token);
            if (result != null)
            {
                return new VectorRecord(
                    result.Id ?? id,
                    result.Vector ?? Array.Empty<float>(),
                    result.Properties);
            }
            return null;
        }, "Get", ct);
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ExecuteWithResilienceAsync(async token =>
        {
            var url = $"/v1/objects/{_options.ClassName}/{id}";
            if (!string.IsNullOrEmpty(_options.Tenant))
            {
                url += $"?tenant={Uri.EscapeDataString(_options.Tenant)}";
            }

            using var request = CreateRequest(HttpMethod.Delete, url);
            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            Metrics.RecordDelete();
        }, "Delete", ct);
    }

    /// <inheritdoc/>
    public override async Task DeleteBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        // Weaviate batch delete uses a different endpoint with match conditions
        foreach (var id in ids)
        {
            await DeleteAsync(id, ct);
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
            var vectorStr = string.Join(",", query);
            var whereClause = filter != null ? BuildWhereClause(filter) : "";
            var tenantClause = !string.IsNullOrEmpty(_options.Tenant)
                ? $"tenant: \"{_options.Tenant}\""
                : "";

            var graphqlQuery = _options.EnableHybridSearch
                ? BuildHybridSearchQuery(vectorStr, topK, whereClause, tenantClause)
                : BuildVectorSearchQuery(vectorStr, topK, whereClause, tenantClause);

            var payload = new { query = graphqlQuery };

            using var request = CreateRequest(HttpMethod.Post, "/v1/graphql");
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync(token);
            var result = JsonSerializer.Deserialize<WeaviateGraphQLResponse>(jsonResponse, JsonOptions);

            var results = new List<VectorSearchResult>();
            var getNode = result?.Data?.Get;
            if (getNode.HasValue && getNode.Value.TryGetProperty(_options.ClassName, out var classArray))
            {
                foreach (var obj in classArray.EnumerateArray())
                {
                    if (obj.TryGetProperty("_additional", out var additional))
                    {
                        var id = additional.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                        var distance = additional.TryGetProperty("distance", out var d) ? d.GetSingle() : 0f;
                        var score = 1.0f - distance;

                        if (score >= minScore)
                        {
                            float[]? vector = null;
                            if (_options.IncludeVectorsInSearch && additional.TryGetProperty("vector", out var v))
                            {
                                vector = JsonSerializer.Deserialize<float[]>(v.GetRawText());
                            }

                            results.Add(new VectorSearchResult(id, score, vector, null));
                        }
                    }
                }
            }

            return results;
        }, "Search", ct);
    }

    private string BuildVectorSearchQuery(string vectorStr, int topK, string whereClause, string tenantClause)
    {
        var additionalFields = _options.IncludeVectorsInSearch ? "id distance vector" : "id distance";

        return $@"{{
            Get {{
                {_options.ClassName}(
                    nearVector: {{ vector: [{vectorStr}] }}
                    limit: {topK}
                    {(string.IsNullOrEmpty(whereClause) ? "" : $"where: {{ {whereClause} }}")}
                    {(string.IsNullOrEmpty(tenantClause) ? "" : tenantClause)}
                ) {{
                    _additional {{ {additionalFields} }}
                }}
            }}
        }}";
    }

    private string BuildHybridSearchQuery(string vectorStr, int topK, string whereClause, string tenantClause)
    {
        var additionalFields = _options.IncludeVectorsInSearch ? "id score vector" : "id score";

        return $@"{{
            Get {{
                {_options.ClassName}(
                    hybrid: {{
                        vector: [{vectorStr}]
                        alpha: {_options.HybridAlpha}
                    }}
                    limit: {topK}
                    {(string.IsNullOrEmpty(whereClause) ? "" : $"where: {{ {whereClause} }}")}
                    {(string.IsNullOrEmpty(tenantClause) ? "" : tenantClause)}
                ) {{
                    _additional {{ {additionalFields} }}
                }}
            }}
        }}";
    }

    private static string BuildWhereClause(Dictionary<string, object> filter)
    {
        // Build Weaviate where clause format
        var conditions = filter.Select(kvp =>
        {
            var value = kvp.Value is string s ? $"\"{s}\"" : kvp.Value.ToString();
            return $"{{ path: [\"{kvp.Key}\"], operator: Equal, valueText: {value} }}";
        });

        return $"operator: And, operands: [{string.Join(", ", conditions)}]";
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
                DistanceMetric.Cosine => "cosine",
                DistanceMetric.Euclidean => "l2-squared",
                DistanceMetric.DotProduct => "dot",
                DistanceMetric.Manhattan => "manhattan",
                _ => "cosine"
            };

            var payload = new
            {
                @class = name,
                vectorIndexConfig = new
                {
                    distance = distanceName
                },
                properties = Array.Empty<object>()
            };

            using var request = CreateRequest(HttpMethod.Post, "/v1/schema");
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
            using var request = CreateRequest(HttpMethod.Delete, $"/v1/schema/{name}");
            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
        }, "DeleteCollection", ct);
    }

    /// <inheritdoc/>
    public override async Task<bool> CollectionExistsAsync(string name, CancellationToken ct = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, $"/v1/schema/{name}");
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
            // Get object count via aggregate query
            var countQuery = $@"{{
                Aggregate {{
                    {_options.ClassName} {{
                        meta {{ count }}
                    }}
                }}
            }}";

            var payload = new { query = countQuery };

            using var request = CreateRequest(HttpMethod.Post, "/v1/graphql");
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync(token);
            var result = JsonSerializer.Deserialize<WeaviateAggregateResponse>(jsonResponse, JsonOptions);

            var count = 0L;
            var aggregateNode = result?.Data?.Aggregate;
            if (aggregateNode.HasValue && aggregateNode.Value.TryGetProperty(_options.ClassName, out var classArray))
            {
                foreach (var obj in classArray.EnumerateArray())
                {
                    if (obj.TryGetProperty("meta", out var meta) && meta.TryGetProperty("count", out var countProp))
                    {
                        count = countProp.GetInt64();
                    }
                }
            }

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
                    ["className"] = _options.ClassName,
                    ["hybridEnabled"] = _options.EnableHybridSearch
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

            using var request = CreateRequest(HttpMethod.Get, "/v1/.well-known/ready");
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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #region Request/Response Models

    private sealed class WeaviateObject
    {
        public string Class { get; set; } = "";
        public string? Id { get; set; }
        public float[]? Vector { get; set; }
        public Dictionary<string, object>? Properties { get; set; }
        public string? Tenant { get; set; }
    }

    private sealed class WeaviateGraphQLResponse
    {
        public WeaviateData? Data { get; set; }
    }

    private sealed class WeaviateData
    {
        public JsonElement? Get { get; set; }
    }

    private sealed class WeaviateAggregateResponse
    {
        public WeaviateAggregateData? Data { get; set; }
    }

    private sealed class WeaviateAggregateData
    {
        public JsonElement? Aggregate { get; set; }
    }

    #endregion
}
