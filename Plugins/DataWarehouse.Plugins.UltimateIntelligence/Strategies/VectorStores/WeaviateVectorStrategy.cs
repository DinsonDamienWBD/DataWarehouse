using System.Net.Http.Json;
using System.Text.Json;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.VectorStores;

/// <summary>
/// Weaviate vector database strategy.
/// Open-source vector database with GraphQL interface and built-in ML models.
/// </summary>
public sealed class WeaviateVectorStrategy : VectorStoreStrategyBase
{
    private const string DefaultHost = "http://localhost:8080";
    private const string DefaultClassName = "DataWarehouse";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "vector-weaviate";

    /// <inheritdoc/>
    public override string StrategyName => "Weaviate Vector Store";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Weaviate",
        Description = "Open-source vector database with GraphQL interface and built-in vectorization",
        Capabilities = IntelligenceCapabilities.AllVectorStore | IntelligenceCapabilities.SemanticSearch,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "Host", Description = "Weaviate host URL", Required = false, DefaultValue = DefaultHost },
            new ConfigurationRequirement { Key = "ApiKey", Description = "Weaviate API key (for cloud)", Required = false, IsSecret = true },
            new ConfigurationRequirement { Key = "ClassName", Description = "Class name for objects", Required = false, DefaultValue = DefaultClassName }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = true,
        Tags = new[] { "weaviate", "vector-db", "open-source", "graphql", "self-hosted" }
    };

    public WeaviateVectorStrategy() : this(new HttpClient()) { }

    public WeaviateVectorStrategy(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    private string GetHost() => GetConfig("Host") ?? DefaultHost;
    private string GetClassName() => GetConfig("ClassName") ?? DefaultClassName;

    /// <inheritdoc/>
    public override async Task StoreAsync(string id, float[] vector, Dictionary<string, object>? metadata = null, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var host = GetHost();
            var className = GetClassName();

            var payload = new
            {
                @class = className,
                id,
                vector,
                properties = metadata ?? new Dictionary<string, object>()
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{host}/v1/objects");
            AddAuthHeader(request);
            request.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            RecordVectorsStored(1);
        });
    }

    /// <inheritdoc/>
    public override async Task StoreBatchAsync(IEnumerable<VectorEntry> entries, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var host = GetHost();
            var className = GetClassName();

            var objects = entries.Select(e => new
            {
                @class = className,
                id = e.Id,
                vector = e.Vector,
                properties = e.Metadata
            }).ToArray();

            var payload = new { objects };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{host}/v1/batch/objects");
            AddAuthHeader(request);
            request.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            RecordVectorsStored(objects.Length);
        });
    }

    /// <inheritdoc/>
    public override async Task<VectorEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var host = GetHost();
            var className = GetClassName();

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{host}/v1/objects/{className}/{id}?include=vector");
            AddAuthHeader(request);

            using var response = await _httpClient.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<WeaviateObject>(cancellationToken: ct);
            if (result == null)
                return null;

            return new VectorEntry
            {
                Id = result.Id ?? id,
                Vector = result.Vector ?? Array.Empty<float>(),
                Metadata = result.Properties ?? new Dictionary<string, object>()
            };
        });
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var host = GetHost();
            var className = GetClassName();

            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{host}/v1/objects/{className}/{id}");
            AddAuthHeader(request);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        });
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<VectorMatch>> SearchAsync(
        float[] query,
        int topK = 10,
        float minScore = 0.0f,
        Dictionary<string, object>? filter = null,
        CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var host = GetHost();
            var className = GetClassName();

            // Use GraphQL for vector search
            var graphql = new
            {
                query = $@"
                {{
                    Get {{
                        {className}(
                            nearVector: {{
                                vector: [{string.Join(",", query)}]
                            }}
                            limit: {topK}
                        ) {{
                            _additional {{
                                id
                                distance
                                vector
                            }}
                        }}
                    }}
                }}"
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{host}/v1/graphql");
            AddAuthHeader(request);
            request.Content = JsonContent.Create(graphql);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<WeaviateGraphQLResponse>(cancellationToken: ct);
            RecordSearch();

            var results = new List<VectorMatch>();
            var objects = result?.Data?.Get?.GetValueOrDefault(className);

            if (objects.HasValue)
            {
                int rank = 1;
                foreach (var obj in objects.Value.EnumerateArray())
                {
                    var additional = obj.GetProperty("_additional");
                    var id = additional.GetProperty("id").GetString() ?? "";
                    var distance = additional.TryGetProperty("distance", out var d) ? d.GetSingle() : 0f;
                    var score = 1.0f - distance; // Convert distance to similarity

                    if (score >= minScore)
                    {
                        var vector = additional.TryGetProperty("vector", out var v)
                            ? v.Deserialize<float[]>() ?? Array.Empty<float>()
                            : Array.Empty<float>();

                        results.Add(new VectorMatch
                        {
                            Entry = new VectorEntry
                            {
                                Id = id,
                                Vector = vector,
                                Metadata = new Dictionary<string, object>()
                            },
                            Score = score,
                            Rank = rank++
                        });
                    }
                }
            }

            return results;
        });
    }

    /// <inheritdoc/>
    public override async Task<long> CountAsync(CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var host = GetHost();
            var className = GetClassName();

            var graphql = new
            {
                query = $@"
                {{
                    Aggregate {{
                        {className} {{
                            meta {{
                                count
                            }}
                        }}
                    }}
                }}"
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{host}/v1/graphql");
            AddAuthHeader(request);
            request.Content = JsonContent.Create(graphql);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<WeaviateAggregateResponse>(cancellationToken: ct);
            return result?.Data?.Aggregate?.GetValueOrDefault(className)?.FirstOrDefault()?.Meta?.Count ?? 0;
        });
    }

    private void AddAuthHeader(HttpRequestMessage request)
    {
        if (GetConfig("ApiKey") is string apiKey && !string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
        }
    }

    // Response models
    private sealed class WeaviateObject
    {
        public string? Id { get; set; }
        public float[]? Vector { get; set; }
        public Dictionary<string, object>? Properties { get; set; }
    }

    private sealed class WeaviateGraphQLResponse
    {
        public WeaviateData? Data { get; set; }
    }

    private sealed class WeaviateData
    {
        public Dictionary<string, JsonElement>? Get { get; set; }
    }

    private sealed class WeaviateAggregateResponse
    {
        public WeaviateAggregateData? Data { get; set; }
    }

    private sealed class WeaviateAggregateData
    {
        public Dictionary<string, List<WeaviateAggregate>>? Aggregate { get; set; }
    }

    private sealed class WeaviateAggregate
    {
        public WeaviateMeta? Meta { get; set; }
    }

    private sealed class WeaviateMeta
    {
        public long Count { get; set; }
    }
}
