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
/// Configuration for Pinecone vector store.
/// </summary>
public sealed class PineconeOptions : VectorStoreOptions
{
    /// <summary>Pinecone API key.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Pinecone environment (e.g., "us-west1-gcp", "us-east-1-aws").</summary>
    public required string Environment { get; init; }

    /// <summary>Index name.</summary>
    public required string IndexName { get; init; }

    /// <summary>Optional namespace for vector operations.</summary>
    public string? Namespace { get; init; }

    /// <summary>Project ID for serverless indexes.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Whether this is a serverless index.</summary>
    public bool IsServerless { get; init; }
}

/// <summary>
/// Production-ready Pinecone vector database connector.
/// </summary>
/// <remarks>
/// Pinecone is a managed vector database optimized for production ML workloads.
/// Features:
/// <list type="bullet">
/// <item>Serverless and pod-based deployment options</item>
/// <item>Namespace support for multi-tenancy</item>
/// <item>Rich metadata filtering with $eq, $ne, $gt, $gte, $lt, $lte, $in, $nin</item>
/// <item>Hybrid search with sparse-dense vectors</item>
/// <item>Automatic scaling and high availability</item>
/// </list>
/// </remarks>
public sealed class PineconeVectorStore : ProductionVectorStoreBase
{
    private readonly PineconeOptions _options;
    private readonly string _indexHost;
    private int _dimensions;

    /// <inheritdoc/>
    public override string StoreId => $"pinecone-{_options.IndexName}";

    /// <inheritdoc/>
    public override string DisplayName => $"Pinecone ({_options.IndexName})";

    /// <inheritdoc/>
    public override int VectorDimensions => _dimensions;

    /// <summary>
    /// Creates a new Pinecone vector store instance.
    /// </summary>
    /// <param name="options">Pinecone configuration options.</param>
    /// <param name="httpClient">Optional HTTP client for testing.</param>
    public PineconeVectorStore(PineconeOptions options, HttpClient? httpClient = null)
        : base(httpClient, options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("API key is required", nameof(options));

        _indexHost = options.IsServerless
            ? $"https://{options.IndexName}-{options.ProjectId}.svc.{options.Environment}.pinecone.io"
            : $"https://{options.IndexName}-{options.Environment}.svc.pinecone.io";
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string endpoint)
    {
        var request = new HttpRequestMessage(method, $"{_indexHost}{endpoint}");
        request.Headers.Add("Api-Key", _options.ApiKey);
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
            var payload = new PineconeUpsertRequest
            {
                Vectors = new[]
                {
                    new PineconeVector
                    {
                        Id = id,
                        Values = vector,
                        Metadata = metadata
                    }
                },
                Namespace = _options.Namespace ?? ""
            };

            using var request = CreateRequest(HttpMethod.Post, "/vectors/upsert");
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
                var vectors = batch.Select(r => new PineconeVector
                {
                    Id = r.Id,
                    Values = r.Vector,
                    Metadata = r.Metadata
                }).ToArray();

                var payload = new PineconeUpsertRequest
                {
                    Vectors = vectors,
                    Namespace = _options.Namespace ?? ""
                };

                using var request = CreateRequest(HttpMethod.Post, "/vectors/upsert");
                request.Content = JsonContent.Create(payload, options: JsonOptions);

                using var response = await HttpClient.SendAsync(request, token);
                response.EnsureSuccessStatusCode();

                Metrics.RecordUpsert(TimeSpan.Zero, vectors.Length - 1); // -1 because base records 1
            }, "UpsertBatch", ct);
        }
    }

    /// <inheritdoc/>
    public override async Task<VectorRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        return await ExecuteWithResilienceAsync(async token =>
        {
            var ns = _options.Namespace ?? "";
            var url = $"/vectors/fetch?ids={Uri.EscapeDataString(id)}&namespace={Uri.EscapeDataString(ns)}";

            using var request = CreateRequest(HttpMethod.Get, url);
            using var response = await HttpClient.SendAsync(request, token);

            if (!response.IsSuccessStatusCode)
                return null;

            var result = await response.Content.ReadFromJsonAsync<PineconeFetchResponse>(JsonOptions, token);
            if (result?.Vectors?.TryGetValue(id, out var vector) == true)
            {
                return new VectorRecord(
                    id,
                    vector.Values ?? Array.Empty<float>(),
                    vector.Metadata);
            }
            return null;
        }, "Get", ct);
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ExecuteWithResilienceAsync(async token =>
        {
            var payload = new PineconeDeleteRequest
            {
                Ids = new[] { id },
                Namespace = _options.Namespace ?? ""
            };

            using var request = CreateRequest(HttpMethod.Post, "/vectors/delete");
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
                var payload = new PineconeDeleteRequest
                {
                    Ids = batchIds,
                    Namespace = _options.Namespace ?? ""
                };

                using var request = CreateRequest(HttpMethod.Post, "/vectors/delete");
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
            var payload = new PineconeQueryRequest
            {
                Vector = query,
                TopK = topK,
                IncludeMetadata = true,
                IncludeValues = _options.IncludeVectorsInSearch,
                Namespace = _options.Namespace ?? "",
                Filter = filter
            };

            using var request = CreateRequest(HttpMethod.Post, "/query");
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PineconeQueryResponse>(JsonOptions, token);

            return result?.Matches?
                .Where(m => m.Score >= minScore)
                .Select(m => new VectorSearchResult(
                    m.Id ?? "",
                    m.Score,
                    m.Values,
                    m.Metadata))
                .ToList() ?? new List<VectorSearchResult>();
        }, "Search", ct);
    }

    /// <inheritdoc/>
    public override async Task CreateCollectionAsync(
        string name,
        int dimensions,
        DistanceMetric metric = DistanceMetric.Cosine,
        CancellationToken ct = default)
    {
        // Pinecone indexes are created via the control plane API
        // This is a no-op as the index should already exist
        _dimensions = dimensions;
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override async Task DeleteCollectionAsync(string name, CancellationToken ct = default)
    {
        // Delete all vectors in namespace
        await ExecuteWithResilienceAsync(async token =>
        {
            var payload = new PineconeDeleteRequest
            {
                DeleteAll = true,
                Namespace = name
            };

            using var request = CreateRequest(HttpMethod.Post, "/vectors/delete");
            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
        }, "DeleteCollection", ct);
    }

    /// <inheritdoc/>
    public override async Task<bool> CollectionExistsAsync(string name, CancellationToken ct = default)
    {
        // Check if namespace has vectors
        var stats = await GetStatisticsAsync(ct);
        return stats.TotalVectors > 0;
    }

    /// <inheritdoc/>
    public override async Task<VectorStoreStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        return await ExecuteWithResilienceAsync(async token =>
        {
            using var request = CreateRequest(HttpMethod.Post, "/describe_index_stats");
            request.Content = JsonContent.Create(new { }, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PineconeStatsResponse>(JsonOptions, token);

            _dimensions = result?.Dimension ?? 0;

            return new VectorStoreStatistics
            {
                TotalVectors = result?.TotalVectorCount ?? 0,
                Dimensions = result?.Dimension ?? 0,
                CollectionCount = result?.Namespaces?.Count ?? 1,
                QueryCount = Metrics.QueryCount,
                AverageQueryLatencyMs = Metrics.AverageQueryLatencyMs,
                IndexType = result?.IndexFullness > 0 ? "HNSW" : "Unknown",
                Metric = DistanceMetric.Cosine,
                ExtendedStats = new Dictionary<string, object>
                {
                    ["indexFullness"] = result?.IndexFullness ?? 0,
                    ["namespaces"] = result?.Namespaces?.Keys.ToList() ?? new List<string>()
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

            using var request = CreateRequest(HttpMethod.Post, "/describe_index_stats");
            request.Content = JsonContent.Create(new { }, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            Debug.WriteLine($"Caught exception in PineconeVectorStore.cs");
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #region Request/Response Models

    private sealed class PineconeUpsertRequest
    {
        public PineconeVector[] Vectors { get; set; } = Array.Empty<PineconeVector>();
        public string Namespace { get; set; } = "";
    }

    private sealed class PineconeVector
    {
        public string Id { get; set; } = "";
        public float[] Values { get; set; } = Array.Empty<float>();
        public Dictionary<string, object>? Metadata { get; set; }
    }

    private sealed class PineconeDeleteRequest
    {
        public string[]? Ids { get; set; }
        public bool? DeleteAll { get; set; }
        public string Namespace { get; set; } = "";
    }

    private sealed class PineconeQueryRequest
    {
        public float[] Vector { get; set; } = Array.Empty<float>();
        public int TopK { get; set; }
        public bool IncludeMetadata { get; set; }
        public bool IncludeValues { get; set; }
        public string Namespace { get; set; } = "";
        public Dictionary<string, object>? Filter { get; set; }
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

    private sealed class PineconeFetchResponse
    {
        public Dictionary<string, PineconeVector>? Vectors { get; set; }
    }

    private sealed class PineconeStatsResponse
    {
        public long TotalVectorCount { get; set; }
        public int Dimension { get; set; }
        public float IndexFullness { get; set; }
        public Dictionary<string, PineconeNamespaceStats>? Namespaces { get; set; }
    }

    private sealed class PineconeNamespaceStats
    {
        public long VectorCount { get; set; }
    }

    #endregion
}
