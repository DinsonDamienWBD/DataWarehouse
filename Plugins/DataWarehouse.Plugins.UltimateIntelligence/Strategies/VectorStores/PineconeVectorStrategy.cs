using System.Net.Http.Json;
using System.Text.Json;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.VectorStores;

/// <summary>
/// Pinecone vector database strategy.
/// High-performance managed vector database for production workloads.
/// </summary>
public sealed class PineconeVectorStrategy : VectorStoreStrategyBase
{
    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "vector-pinecone";

    /// <inheritdoc/>
    public override string StrategyName => "Pinecone Vector Store";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Pinecone",
        Description = "High-performance managed vector database with hybrid search capabilities",
        Capabilities = IntelligenceCapabilities.AllVectorStore | IntelligenceCapabilities.SemanticSearch,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ApiKey", Description = "Pinecone API key", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "Environment", Description = "Pinecone environment (e.g., us-west1-gcp)", Required = true },
            new ConfigurationRequirement { Key = "IndexName", Description = "Index name", Required = true },
            new ConfigurationRequirement { Key = "Namespace", Description = "Namespace for vector operations", Required = false }
        },
        CostTier = 3,
        LatencyTier = 1,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "pinecone", "vector-db", "managed", "hybrid-search", "production" }
    };

    private static readonly HttpClient SharedHttpClient = new HttpClient();
    public PineconeVectorStrategy() : this(SharedHttpClient) { }

    public PineconeVectorStrategy(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    private string GetIndexHost()
    {
        var environment = GetRequiredConfig("Environment");
        var indexName = GetRequiredConfig("IndexName");
        return $"https://{indexName}-{environment}.svc.pinecone.io";
    }

    /// <inheritdoc/>
    public override async Task StoreAsync(string id, float[] vector, Dictionary<string, object>? metadata = null, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var apiKey = GetRequiredConfig("ApiKey");
            var ns = GetConfig("Namespace") ?? "";

            var payload = new
            {
                vectors = new[]
                {
                    new
                    {
                        id,
                        values = vector,
                        metadata
                    }
                },
                @namespace = ns
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetIndexHost()}/vectors/upsert");
            request.Headers.Add("Api-Key", apiKey);
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
            var apiKey = GetRequiredConfig("ApiKey");
            var ns = GetConfig("Namespace") ?? "";

            var vectors = entries.Select(e => new
            {
                id = e.Id,
                values = e.Vector,
                metadata = e.Metadata
            }).ToArray();

            var payload = new
            {
                vectors,
                @namespace = ns
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetIndexHost()}/vectors/upsert");
            request.Headers.Add("Api-Key", apiKey);
            request.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            RecordVectorsStored(vectors.Length);
        });
    }

    /// <inheritdoc/>
    public override async Task<VectorEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var apiKey = GetRequiredConfig("ApiKey");
            var ns = GetConfig("Namespace") ?? "";

            var url = $"{GetIndexHost()}/vectors/fetch?ids={id}&namespace={ns}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Api-Key", apiKey);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PineconeFetchResponse>(cancellationToken: ct);
            if (result?.Vectors?.TryGetValue(id, out var vector) == true)
            {
                return new VectorEntry
                {
                    Id = id,
                    Vector = vector.Values ?? Array.Empty<float>(),
                    Metadata = vector.Metadata ?? new Dictionary<string, object>()
                };
            }
            return null;
        });
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var apiKey = GetRequiredConfig("ApiKey");
            var ns = GetConfig("Namespace") ?? "";

            var payload = new
            {
                ids = new[] { id },
                @namespace = ns
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetIndexHost()}/vectors/delete");
            request.Headers.Add("Api-Key", apiKey);
            request.Content = JsonContent.Create(payload);

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
            var apiKey = GetRequiredConfig("ApiKey");
            var ns = GetConfig("Namespace") ?? "";

            var payload = new
            {
                vector = query,
                topK,
                includeMetadata = true,
                includeValues = true,
                @namespace = ns,
                filter
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetIndexHost()}/query");
            request.Headers.Add("Api-Key", apiKey);
            request.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PineconeQueryResponse>(cancellationToken: ct);
            RecordSearch();

            return result?.Matches?
                .Where(m => m.Score >= minScore)
                .Select((m, i) => new VectorMatch
                {
                    Entry = new VectorEntry
                    {
                        Id = m.Id ?? "",
                        Vector = m.Values ?? Array.Empty<float>(),
                        Metadata = m.Metadata ?? new Dictionary<string, object>()
                    },
                    Score = m.Score,
                    Rank = i + 1
                })
                .ToList() ?? new List<VectorMatch>();
        });
    }

    /// <inheritdoc/>
    public override async Task<long> CountAsync(CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var apiKey = GetRequiredConfig("ApiKey");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetIndexHost()}/describe_index_stats");
            request.Headers.Add("Api-Key", apiKey);
            request.Content = JsonContent.Create(new { });

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PineconeStatsResponse>(cancellationToken: ct);
            return result?.TotalVectorCount ?? 0;
        });
    }

    // Response models
    private sealed class PineconeFetchResponse
    {
        public Dictionary<string, PineconeVector>? Vectors { get; set; }
    }

    private sealed class PineconeVector
    {
        public float[]? Values { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }
    }

    private sealed class PineconeQueryResponse
    {
        public List<PineconeMatch>? Matches { get; set; }
    }

    private sealed class PineconeMatch
    {
        public string? Id { get; set; }
        public float Score { get; set; }
        public float[]? Values { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }
    }

    private sealed class PineconeStatsResponse
    {
        public long TotalVectorCount { get; set; }
    }
}
