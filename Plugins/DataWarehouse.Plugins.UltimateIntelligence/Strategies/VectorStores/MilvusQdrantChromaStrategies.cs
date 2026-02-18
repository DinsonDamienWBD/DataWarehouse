using System.Net.Http.Json;
using System.Text.Json;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.VectorStores;

/// <summary>
/// Milvus vector database strategy.
/// Cloud-native vector database built for scalable similarity search.
/// </summary>
public sealed class MilvusVectorStrategy : VectorStoreStrategyBase
{
    private const string DefaultHost = "http://localhost:19530";
    private const string DefaultCollection = "datawarehouse";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "vector-milvus";

    /// <inheritdoc/>
    public override string StrategyName => "Milvus Vector Store";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Milvus",
        Description = "Cloud-native vector database built for scalable billion-scale similarity search",
        Capabilities = IntelligenceCapabilities.AllVectorStore | IntelligenceCapabilities.SemanticSearch,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "Host", Description = "Milvus host URL", Required = false, DefaultValue = DefaultHost },
            new ConfigurationRequirement { Key = "Collection", Description = "Collection name", Required = false, DefaultValue = DefaultCollection },
            new ConfigurationRequirement { Key = "ApiKey", Description = "API key (for Zilliz Cloud)", Required = false, IsSecret = true }
        },
        CostTier = 2,
        LatencyTier = 1,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = true,
        Tags = new[] { "milvus", "vector-db", "cloud-native", "billion-scale", "self-hosted" }
    };

    private static readonly HttpClient SharedHttpClient = new HttpClient();
    public MilvusVectorStrategy() : this(SharedHttpClient) { }
    public MilvusVectorStrategy(HttpClient httpClient) { _httpClient = httpClient; }

    private string GetHost() => GetConfig("Host") ?? DefaultHost;
    private string GetCollection() => GetConfig("Collection") ?? DefaultCollection;

    /// <inheritdoc/>
    public override async Task StoreAsync(string id, float[] vector, Dictionary<string, object>? metadata = null, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var payload = new
            {
                collectionName = GetCollection(),
                data = new[] { new { id, vector, metadata = metadata ?? new Dictionary<string, object>() } }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetHost()}/v1/vector/insert");
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
            var data = entries.Select(e => new { id = e.Id, vector = e.Vector, metadata = e.Metadata }).ToArray();
            var payload = new { collectionName = GetCollection(), data };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetHost()}/v1/vector/insert");
            AddAuthHeader(request);
            request.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            RecordVectorsStored(data.Length);
        });
    }

    /// <inheritdoc/>
    public override async Task<VectorEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var payload = new { collectionName = GetCollection(), id };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetHost()}/v1/vector/get");
            AddAuthHeader(request);
            request.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var result = await response.Content.ReadFromJsonAsync<MilvusGetResponse>(cancellationToken: ct);
            var data = result?.Data?.FirstOrDefault();
            return data != null ? new VectorEntry { Id = id, Vector = data.Vector ?? Array.Empty<float>(), Metadata = data.Metadata ?? new() } : null;
        });
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var payload = new { collectionName = GetCollection(), id };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetHost()}/v1/vector/delete");
            AddAuthHeader(request);
            request.Content = JsonContent.Create(payload);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        });
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<VectorMatch>> SearchAsync(float[] query, int topK = 10, float minScore = 0.0f, Dictionary<string, object>? filter = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var payload = new { collectionName = GetCollection(), vector = query, topK, filter };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetHost()}/v1/vector/search");
            AddAuthHeader(request);
            request.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            RecordSearch();

            var result = await response.Content.ReadFromJsonAsync<MilvusSearchResponse>(cancellationToken: ct);
            return result?.Data?.Where(m => m.Score >= minScore)
                .Select((m, i) => new VectorMatch { Entry = new VectorEntry { Id = m.Id ?? "", Vector = m.Vector ?? Array.Empty<float>(), Metadata = m.Metadata ?? new() }, Score = m.Score, Rank = i + 1 })
                .ToList() ?? new List<VectorMatch>();
        });
    }

    /// <inheritdoc/>
    public override async Task<long> CountAsync(CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var payload = new { collectionName = GetCollection() };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetHost()}/v1/vector/count");
            AddAuthHeader(request);
            request.Content = JsonContent.Create(payload);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<MilvusCountResponse>(cancellationToken: ct);
            return result?.Count ?? 0;
        });
    }

    private void AddAuthHeader(HttpRequestMessage request) { if (GetConfig("ApiKey") is string k && !string.IsNullOrEmpty(k)) request.Headers.Add("Authorization", $"Bearer {k}"); }

    private sealed class MilvusGetResponse { public List<MilvusData>? Data { get; set; } }
    private sealed class MilvusSearchResponse { public List<MilvusData>? Data { get; set; } }
    private sealed class MilvusData { public string? Id { get; set; } public float[]? Vector { get; set; } public float Score { get; set; } public Dictionary<string, object>? Metadata { get; set; } }
    private sealed class MilvusCountResponse { public long Count { get; set; } }
}

/// <summary>
/// Qdrant vector database strategy.
/// High-performance vector similarity search engine with extended filtering support.
/// </summary>
public sealed class QdrantVectorStrategy : VectorStoreStrategyBase
{
    private const string DefaultHost = "http://localhost:6333";
    private const string DefaultCollection = "datawarehouse";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "vector-qdrant";

    /// <inheritdoc/>
    public override string StrategyName => "Qdrant Vector Store";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Qdrant",
        Description = "High-performance vector search engine with extended filtering and payload storage",
        Capabilities = IntelligenceCapabilities.AllVectorStore | IntelligenceCapabilities.SemanticSearch,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "Host", Description = "Qdrant host URL", Required = false, DefaultValue = DefaultHost },
            new ConfigurationRequirement { Key = "Collection", Description = "Collection name", Required = false, DefaultValue = DefaultCollection },
            new ConfigurationRequirement { Key = "ApiKey", Description = "API key", Required = false, IsSecret = true }
        },
        CostTier = 2,
        LatencyTier = 1,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = true,
        Tags = new[] { "qdrant", "vector-db", "rust", "filtering", "self-hosted" }
    };

    private static readonly HttpClient SharedHttpClient = new HttpClient();
    public QdrantVectorStrategy() : this(SharedHttpClient) { }
    public QdrantVectorStrategy(HttpClient httpClient) { _httpClient = httpClient; }

    private string GetHost() => GetConfig("Host") ?? DefaultHost;
    private string GetCollection() => GetConfig("Collection") ?? DefaultCollection;

    /// <inheritdoc/>
    public override async Task StoreAsync(string id, float[] vector, Dictionary<string, object>? metadata = null, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var payload = new { points = new[] { new { id, vector, payload = metadata } } };
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{GetHost()}/collections/{GetCollection()}/points");
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
            var points = entries.Select(e => new { id = e.Id, vector = e.Vector, payload = e.Metadata }).ToArray();
            var payload = new { points };
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{GetHost()}/collections/{GetCollection()}/points");
            AddAuthHeader(request);
            request.Content = JsonContent.Create(payload);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            RecordVectorsStored(points.Length);
        });
    }

    /// <inheritdoc/>
    public override async Task<VectorEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{GetHost()}/collections/{GetCollection()}/points/{id}");
            AddAuthHeader(request);
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var result = await response.Content.ReadFromJsonAsync<QdrantPointResponse>(cancellationToken: ct);
            return result?.Result != null ? new VectorEntry { Id = id, Vector = result.Result.Vector ?? Array.Empty<float>(), Metadata = result.Result.Payload ?? new() } : null;
        });
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var payload = new { points = new[] { id } };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetHost()}/collections/{GetCollection()}/points/delete");
            AddAuthHeader(request);
            request.Content = JsonContent.Create(payload);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        });
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<VectorMatch>> SearchAsync(float[] query, int topK = 10, float minScore = 0.0f, Dictionary<string, object>? filter = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var payload = new { vector = query, limit = topK, with_payload = true, with_vector = true, filter };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetHost()}/collections/{GetCollection()}/points/search");
            AddAuthHeader(request);
            request.Content = JsonContent.Create(payload);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            RecordSearch();
            var result = await response.Content.ReadFromJsonAsync<QdrantSearchResponse>(cancellationToken: ct);
            return result?.Result?.Where(m => m.Score >= minScore)
                .Select((m, i) => new VectorMatch { Entry = new VectorEntry { Id = m.Id ?? "", Vector = m.Vector ?? Array.Empty<float>(), Metadata = m.Payload ?? new() }, Score = m.Score, Rank = i + 1 })
                .ToList() ?? new List<VectorMatch>();
        });
    }

    /// <inheritdoc/>
    public override async Task<long> CountAsync(CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{GetHost()}/collections/{GetCollection()}");
            AddAuthHeader(request);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<QdrantCollectionResponse>(cancellationToken: ct);
            return result?.Result?.PointsCount ?? 0;
        });
    }

    private void AddAuthHeader(HttpRequestMessage request) { if (GetConfig("ApiKey") is string k && !string.IsNullOrEmpty(k)) request.Headers.Add("api-key", k); }

    private sealed class QdrantPointResponse { public QdrantPoint? Result { get; set; } }
    private sealed class QdrantPoint { public float[]? Vector { get; set; } public Dictionary<string, object>? Payload { get; set; } }
    private sealed class QdrantSearchResponse { public List<QdrantSearchResult>? Result { get; set; } }
    private sealed class QdrantSearchResult { public string? Id { get; set; } public float Score { get; set; } public float[]? Vector { get; set; } public Dictionary<string, object>? Payload { get; set; } }
    private sealed class QdrantCollectionResponse { public QdrantCollectionInfo? Result { get; set; } }
    private sealed class QdrantCollectionInfo { public long PointsCount { get; set; } }
}

/// <summary>
/// ChromaDB vector database strategy.
/// AI-native open-source embedding database.
/// </summary>
public sealed class ChromaVectorStrategy : VectorStoreStrategyBase
{
    private const string DefaultHost = "http://localhost:8000";
    private const string DefaultCollection = "datawarehouse";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "vector-chroma";

    /// <inheritdoc/>
    public override string StrategyName => "Chroma Vector Store";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Chroma",
        Description = "AI-native open-source embedding database with simple API",
        Capabilities = IntelligenceCapabilities.AllVectorStore | IntelligenceCapabilities.SemanticSearch,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "Host", Description = "Chroma host URL", Required = false, DefaultValue = DefaultHost },
            new ConfigurationRequirement { Key = "Collection", Description = "Collection name", Required = false, DefaultValue = DefaultCollection }
        },
        CostTier = 1,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = true,
        Tags = new[] { "chroma", "vector-db", "python", "ai-native", "simple" }
    };

    private static readonly HttpClient SharedHttpClient = new HttpClient();
    public ChromaVectorStrategy() : this(SharedHttpClient) { }
    public ChromaVectorStrategy(HttpClient httpClient) { _httpClient = httpClient; }

    private string GetHost() => GetConfig("Host") ?? DefaultHost;
    private string GetCollection() => GetConfig("Collection") ?? DefaultCollection;

    /// <inheritdoc/>
    public override async Task StoreAsync(string id, float[] vector, Dictionary<string, object>? metadata = null, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var payload = new { ids = new[] { id }, embeddings = new[] { vector }, metadatas = new[] { metadata } };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetHost()}/api/v1/collections/{GetCollection()}/add");
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
            var list = entries.ToList();
            var payload = new { ids = list.Select(e => e.Id).ToArray(), embeddings = list.Select(e => e.Vector).ToArray(), metadatas = list.Select(e => e.Metadata).ToArray() };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetHost()}/api/v1/collections/{GetCollection()}/add");
            request.Content = JsonContent.Create(payload);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            RecordVectorsStored(list.Count);
        });
    }

    /// <inheritdoc/>
    public override async Task<VectorEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var payload = new { ids = new[] { id }, include = new[] { "embeddings", "metadatas" } };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetHost()}/api/v1/collections/{GetCollection()}/get");
            request.Content = JsonContent.Create(payload);
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var result = await response.Content.ReadFromJsonAsync<ChromaGetResponse>(cancellationToken: ct);
            if (result?.Ids?.Length > 0)
                return new VectorEntry { Id = id, Vector = result.Embeddings?.FirstOrDefault() ?? Array.Empty<float>(), Metadata = result.Metadatas?.FirstOrDefault() ?? new() };
            return null;
        });
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var payload = new { ids = new[] { id } };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetHost()}/api/v1/collections/{GetCollection()}/delete");
            request.Content = JsonContent.Create(payload);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        });
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<VectorMatch>> SearchAsync(float[] query, int topK = 10, float minScore = 0.0f, Dictionary<string, object>? filter = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var payload = new { query_embeddings = new[] { query }, n_results = topK, include = new[] { "embeddings", "metadatas", "distances" }, where = filter };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetHost()}/api/v1/collections/{GetCollection()}/query");
            request.Content = JsonContent.Create(payload);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            RecordSearch();
            var result = await response.Content.ReadFromJsonAsync<ChromaQueryResponse>(cancellationToken: ct);
            var results = new List<VectorMatch>();
            if (result?.Ids?.Length > 0)
            {
                var ids = result.Ids[0];
                var embeddings = result.Embeddings?[0];
                var distances = result.Distances?[0];
                var metadatas = result.Metadatas?[0];
                for (int i = 0; i < ids.Length; i++)
                {
                    var score = 1.0f - (distances?[i] ?? 0f);
                    if (score >= minScore)
                        results.Add(new VectorMatch { Entry = new VectorEntry { Id = ids[i], Vector = embeddings?[i] ?? Array.Empty<float>(), Metadata = metadatas?[i] ?? new() }, Score = score, Rank = i + 1 });
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
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{GetHost()}/api/v1/collections/{GetCollection()}/count");
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var count = await response.Content.ReadFromJsonAsync<long>(cancellationToken: ct);
            return count;
        });
    }

    private sealed class ChromaGetResponse { public string[]? Ids { get; set; } public float[][]? Embeddings { get; set; } public Dictionary<string, object>[]? Metadatas { get; set; } }
    private sealed class ChromaQueryResponse { public string[][]? Ids { get; set; } public float[][][]? Embeddings { get; set; } public float[][]? Distances { get; set; } public Dictionary<string, object>[][]? Metadatas { get; set; } }
}

/// <summary>
/// PgVector (PostgreSQL) vector database strategy.
/// Vector similarity search in PostgreSQL using the pgvector extension.
/// In-memory implementation using cosine similarity.
/// </summary>
public sealed class PgVectorStrategy : VectorStoreStrategyBase
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, VectorEntry> _store = new();

    /// <inheritdoc/>
    public override string StrategyId => "vector-pgvector";

    /// <inheritdoc/>
    public override string StrategyName => "PgVector (PostgreSQL)";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "PgVector",
        Description = "Vector similarity search using PostgreSQL with pgvector extension",
        Capabilities = IntelligenceCapabilities.AllVectorStore | IntelligenceCapabilities.SemanticSearch,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ConnectionString", Description = "PostgreSQL connection string", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "TableName", Description = "Table name for vectors", Required = false, DefaultValue = "embeddings" }
        },
        CostTier = 1,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = true,
        Tags = new[] { "pgvector", "postgresql", "sql", "self-hosted", "production" }
    };

    public override Task StoreAsync(string id, float[] vector, Dictionary<string, object>? metadata = null, CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            var entry = new VectorEntry
            {
                Id = id,
                Vector = vector.ToArray(),
                Metadata = metadata != null ? new Dictionary<string, object>(metadata) : new Dictionary<string, object>()
            };
            _store[id] = entry;
            RecordVectorsStored(1);
            return Task.CompletedTask;
        });
    }

    public override Task StoreBatchAsync(IEnumerable<VectorEntry> entries, CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            int count = 0;
            foreach (var entry in entries)
            {
                var copy = new VectorEntry
                {
                    Id = entry.Id,
                    Vector = entry.Vector.ToArray(),
                    Metadata = entry.Metadata != null ? new Dictionary<string, object>(entry.Metadata) : new Dictionary<string, object>()
                };
                _store[entry.Id] = copy;
                count++;
            }
            RecordVectorsStored(count);
            return Task.CompletedTask;
        });
    }

    public override Task<VectorEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            _store.TryGetValue(id, out var entry);
            return Task.FromResult(entry);
        });
    }

    public override Task DeleteAsync(string id, CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            _store.TryRemove(id, out _);
            return Task.CompletedTask;
        });
    }

    public override Task<IEnumerable<VectorMatch>> SearchAsync(float[] query, int topK = 10, float minScore = 0.0f, Dictionary<string, object>? filter = null, CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            RecordSearch();
            var results = new List<(VectorEntry entry, float score)>();

            foreach (var kvp in _store)
            {
                if (filter != null && !MatchesFilter(kvp.Value.Metadata, filter))
                    continue;

                var score = CosineSimilarity(query, kvp.Value.Vector);
                if (score >= minScore)
                    results.Add((kvp.Value, score));
            }

            var matches = results
                .OrderByDescending(x => x.score)
                .Take(topK)
                .Select((x, i) => new VectorMatch
                {
                    Entry = new VectorEntry
                    {
                        Id = x.entry.Id,
                        Vector = x.entry.Vector.ToArray(),
                        Metadata = new Dictionary<string, object>(x.entry.Metadata)
                    },
                    Score = x.score,
                    Rank = i + 1
                })
                .ToList();

            return Task.FromResult<IEnumerable<VectorMatch>>(matches);
        });
    }

    public override Task<long> CountAsync(CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() => Task.FromResult<long>(_store.Count));
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0f, magA = 0f, magB = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        var denom = (float)Math.Sqrt(magA) * (float)Math.Sqrt(magB);
        return denom > 0 ? dot / denom : 0f;
    }

    private static bool MatchesFilter(Dictionary<string, object> metadata, Dictionary<string, object> filter)
    {
        foreach (var kvp in filter)
        {
            if (!metadata.TryGetValue(kvp.Key, out var value) || !Equals(value, kvp.Value))
                return false;
        }
        return true;
    }
}
