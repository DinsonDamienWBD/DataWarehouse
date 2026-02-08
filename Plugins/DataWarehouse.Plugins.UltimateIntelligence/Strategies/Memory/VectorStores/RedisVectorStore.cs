using System;
using System.Collections.Generic;
using System.Globalization;
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
/// Index algorithm for Redis vector search.
/// </summary>
public enum RedisIndexAlgorithm
{
    /// <summary>Flat brute-force search (highest accuracy, slower).</summary>
    Flat,

    /// <summary>HNSW approximate search (faster, configurable accuracy).</summary>
    Hnsw
}

/// <summary>
/// Configuration for Redis vector store.
/// </summary>
public sealed class RedisOptions : VectorStoreOptions
{
    /// <summary>Redis connection string or URL.</summary>
    public string ConnectionString { get; init; } = "localhost:6379";

    /// <summary>Index name for vector search.</summary>
    public string IndexName { get; init; } = "datawarehouse_idx";

    /// <summary>Key prefix for vectors.</summary>
    public string KeyPrefix { get; init; } = "vec:";

    /// <summary>Redis password.</summary>
    public string? Password { get; init; }

    /// <summary>Index algorithm to use.</summary>
    public RedisIndexAlgorithm Algorithm { get; init; } = RedisIndexAlgorithm.Hnsw;

    /// <summary>HNSW M parameter (number of bi-directional links).</summary>
    public int HnswM { get; init; } = 16;

    /// <summary>HNSW efConstruction parameter.</summary>
    public int HnswEfConstruction { get; init; } = 200;

    /// <summary>HNSW efRuntime parameter for search.</summary>
    public int HnswEfRuntime { get; init; } = 10;

    /// <summary>Initial capacity for the index.</summary>
    public int InitialCapacity { get; init; } = 10000;

    /// <summary>Redis REST API endpoint (for Redis Cloud or RedisInsight).</summary>
    public string? RestApiEndpoint { get; init; }
}

/// <summary>
/// Production-ready Redis Stack vector search connector.
/// </summary>
/// <remarks>
/// Redis Stack includes RediSearch module for vector similarity search.
/// Features:
/// <list type="bullet">
/// <item>HNSW and FLAT index algorithms</item>
/// <item>Hybrid queries combining FT.SEARCH with vector similarity</item>
/// <item>Low latency in-memory search</item>
/// <item>Filtering on Hash or JSON fields</item>
/// <item>Range queries and full-text search combined with vectors</item>
/// </list>
/// Note: This implementation uses Redis REST API. For production, consider using
/// StackExchange.Redis with NRedisStack for native protocol performance.
/// </remarks>
public sealed class RedisVectorStore : ProductionVectorStoreBase
{
    private readonly RedisOptions _options;
    private readonly string _restApiEndpoint;
    private int _dimensions;
#pragma warning disable CS0414 // Field is assigned but never read - used for internal state tracking
    private bool _indexCreated;
#pragma warning restore CS0414

    /// <inheritdoc/>
    public override string StoreId => $"redis-{_options.IndexName}";

    /// <inheritdoc/>
    public override string DisplayName => $"Redis Stack ({_options.IndexName})";

    /// <inheritdoc/>
    public override int VectorDimensions => _dimensions;

    /// <summary>
    /// Creates a new Redis vector store instance.
    /// </summary>
    /// <param name="options">Redis configuration options.</param>
    /// <param name="httpClient">Optional HTTP client for testing.</param>
    public RedisVectorStore(RedisOptions options, HttpClient? httpClient = null)
        : base(httpClient, options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrEmpty(_options.RestApiEndpoint))
        {
            // Construct REST endpoint from connection string
            var host = _options.ConnectionString.Split(':')[0];
            _restApiEndpoint = $"http://{host}:8001";
        }
        else
        {
            _restApiEndpoint = _options.RestApiEndpoint;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string endpoint)
    {
        var request = new HttpRequestMessage(method, $"{_restApiEndpoint.TrimEnd('/')}{endpoint}");
        if (!string.IsNullOrEmpty(_options.Password))
        {
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"default:{_options.Password}"));
            request.Headers.Add("Authorization", $"Basic {auth}");
        }
        return request;
    }

    private async Task ExecuteCommandAsync(string[] args, CancellationToken ct)
    {
        var payload = new { args };
        using var request = CreateRequest(HttpMethod.Post, "/");
        request.Content = JsonContent.Create(payload, options: JsonOptions);

        using var response = await HttpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> ExecuteCommandWithResultAsync(string[] args, CancellationToken ct)
    {
        var payload = new { args };
        using var request = CreateRequest(HttpMethod.Post, "/");
        request.Content = JsonContent.Create(payload, options: JsonOptions);

        using var response = await HttpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private string VectorToBlob(float[] vector)
    {
        // Convert float array to binary blob (little-endian float32)
        var bytes = new byte[vector.Length * 4];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    private float[] BlobToVector(string blob)
    {
        var bytes = Convert.FromBase64String(blob);
        var vector = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
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
            var key = $"{_options.KeyPrefix}{id}";
            var vectorBlob = VectorToBlob(vector);

            var args = new List<string> { "HSET", key, "vector", vectorBlob, "id", id };

            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    args.Add(kvp.Key);
                    args.Add(kvp.Value?.ToString() ?? "");
                }
            }

            await ExecuteCommandAsync(args.ToArray(), token);
        }, "Upsert", ct);
    }

    /// <inheritdoc/>
    public override async Task UpsertBatchAsync(IEnumerable<VectorRecord> records, CancellationToken ct = default)
    {
        // Redis MSET doesn't support HSET semantics, so we use pipeline via multiple commands
        foreach (var record in records)
        {
            await UpsertAsync(record.Id, record.Vector, record.Metadata, ct);
        }
    }

    /// <inheritdoc/>
    public override async Task<VectorRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        return await ExecuteWithResilienceAsync(async token =>
        {
            var key = $"{_options.KeyPrefix}{id}";
            var result = await ExecuteCommandWithResultAsync(new[] { "HGETALL", key }, token);

            if (result.ValueKind == JsonValueKind.Array && result.GetArrayLength() > 0)
            {
                var dict = new Dictionary<string, string>();
                var arr = result.EnumerateArray().ToArray();

                for (int i = 0; i < arr.Length - 1; i += 2)
                {
                    dict[arr[i].GetString() ?? ""] = arr[i + 1].GetString() ?? "";
                }

                if (dict.TryGetValue("vector", out var vectorBlob))
                {
                    var vector = BlobToVector(vectorBlob);
                    var metadata = dict
                        .Where(kvp => kvp.Key != "vector" && kvp.Key != "id")
                        .ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value);

                    return new VectorRecord(id, vector, metadata);
                }
            }
            return null;
        }, "Get", ct);
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ExecuteWithResilienceAsync(async token =>
        {
            var key = $"{_options.KeyPrefix}{id}";
            await ExecuteCommandAsync(new[] { "DEL", key }, token);
            Metrics.RecordDelete();
        }, "Delete", ct);
    }

    /// <inheritdoc/>
    public override async Task DeleteBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var keys = ids.Select(id => $"{_options.KeyPrefix}{id}").ToArray();
        if (keys.Length == 0) return;

        await ExecuteWithResilienceAsync(async token =>
        {
            var args = new[] { "DEL" }.Concat(keys).ToArray();
            await ExecuteCommandAsync(args, token);
            Metrics.RecordDelete(keys.Length);
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
            var vectorBlob = VectorToBlob(query);
            var filterStr = filter != null ? BuildFilter(filter) : "*";

            var knn = $"[KNN {topK} @vector $vec AS score]";
            var returnFields = _options.IncludeVectorsInSearch
                ? new[] { "RETURN", "4", "id", "score", "vector", "$" }
                : new[] { "RETURN", "2", "id", "score" };

            var args = new List<string>
            {
                "FT.SEARCH",
                _options.IndexName,
                $"({filterStr})=>{knn}",
                "PARAMS", "2", "vec", vectorBlob,
                "SORTBY", "score",
                "LIMIT", "0", topK.ToString(),
                "DIALECT", "2"
            };
            args.AddRange(returnFields);

            var result = await ExecuteCommandWithResultAsync(args.ToArray(), token);

            var results = new List<VectorSearchResult>();

            if (result.ValueKind == JsonValueKind.Array)
            {
                var arr = result.EnumerateArray().ToArray();
                if (arr.Length > 1)
                {
                    // First element is total count, then pairs of (key, fields)
                    for (int i = 1; i < arr.Length; i += 2)
                    {
                        var key = arr[i].GetString() ?? "";
                        var id = key.StartsWith(_options.KeyPrefix)
                            ? key[_options.KeyPrefix.Length..]
                            : key;

                        if (i + 1 < arr.Length)
                        {
                            var fields = arr[i + 1].EnumerateArray().ToArray();
                            var fieldDict = new Dictionary<string, string>();

                            for (int j = 0; j < fields.Length - 1; j += 2)
                            {
                                fieldDict[fields[j].GetString() ?? ""] = fields[j + 1].GetString() ?? "";
                            }

                            var score = fieldDict.TryGetValue("score", out var s)
                                ? float.Parse(s, CultureInfo.InvariantCulture)
                                : 0f;

                            // Redis returns distance, convert to similarity
                            var similarity = 1.0f / (1.0f + score);

                            if (similarity >= minScore)
                            {
                                float[]? vector = null;
                                if (_options.IncludeVectorsInSearch && fieldDict.TryGetValue("vector", out var blob))
                                {
                                    vector = BlobToVector(blob);
                                }

                                results.Add(new VectorSearchResult(id, similarity, vector, null));
                            }
                        }
                    }
                }
            }

            return results;
        }, "Search", ct);
    }

    private static string BuildFilter(Dictionary<string, object> filter)
    {
        var conditions = filter.Select(kvp =>
        {
            if (kvp.Value is string s)
                return $"@{kvp.Key}:{{{EscapeTag(s)}}}";
            if (kvp.Value is int or long or float or double)
                return $"@{kvp.Key}:[{kvp.Value} {kvp.Value}]";
            return $"@{kvp.Key}:{{{EscapeTag(kvp.Value?.ToString() ?? "")}}}";
        });

        return string.Join(" ", conditions);
    }

    private static string EscapeTag(string value)
    {
        return value.Replace("-", "\\-").Replace(".", "\\.").Replace(" ", "\\ ");
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
            var distanceMetric = metric switch
            {
                DistanceMetric.Cosine => "COSINE",
                DistanceMetric.Euclidean => "L2",
                DistanceMetric.DotProduct => "IP",
                _ => "COSINE"
            };

            var algorithm = _options.Algorithm == RedisIndexAlgorithm.Hnsw ? "HNSW" : "FLAT";

            var args = new List<string>
            {
                "FT.CREATE",
                name,
                "ON", "HASH",
                "PREFIX", "1", _options.KeyPrefix,
                "SCHEMA",
                "id", "TEXT",
                "vector", "VECTOR", algorithm, "10",
                "TYPE", "FLOAT32",
                "DIM", dimensions.ToString(),
                "DISTANCE_METRIC", distanceMetric,
                "INITIAL_CAP", _options.InitialCapacity.ToString()
            };

            if (_options.Algorithm == RedisIndexAlgorithm.Hnsw)
            {
                args.AddRange(new[]
                {
                    "M", _options.HnswM.ToString(),
                    "EF_CONSTRUCTION", _options.HnswEfConstruction.ToString(),
                    "EF_RUNTIME", _options.HnswEfRuntime.ToString()
                });
            }

            await ExecuteCommandAsync(args.ToArray(), token);

            _dimensions = dimensions;
            _indexCreated = true;
        }, "CreateCollection", ct);
    }

    /// <inheritdoc/>
    public override async Task DeleteCollectionAsync(string name, CancellationToken ct = default)
    {
        await ExecuteWithResilienceAsync(async token =>
        {
            await ExecuteCommandAsync(new[] { "FT.DROPINDEX", name, "DD" }, token);

            if (name == _options.IndexName)
            {
                _indexCreated = false;
            }
        }, "DeleteCollection", ct);
    }

    /// <inheritdoc/>
    public override async Task<bool> CollectionExistsAsync(string name, CancellationToken ct = default)
    {
        try
        {
            await ExecuteCommandWithResultAsync(new[] { "FT.INFO", name }, ct);
            return true;
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
            var result = await ExecuteCommandWithResultAsync(new[] { "FT.INFO", _options.IndexName }, token);

            long numDocs = 0;
            int dim = _dimensions;

            if (result.ValueKind == JsonValueKind.Array)
            {
                var arr = result.EnumerateArray().ToArray();
                for (int i = 0; i < arr.Length - 1; i += 2)
                {
                    var key = arr[i].GetString();
                    if (key == "num_docs" && arr[i + 1].ValueKind == JsonValueKind.Number)
                    {
                        numDocs = arr[i + 1].GetInt64();
                    }
                }
            }

            return new VectorStoreStatistics
            {
                TotalVectors = numDocs,
                Dimensions = dim,
                CollectionCount = 1,
                QueryCount = Metrics.QueryCount,
                AverageQueryLatencyMs = Metrics.AverageQueryLatencyMs,
                IndexType = _options.Algorithm.ToString().ToUpperInvariant(),
                Metric = DistanceMetric.Cosine,
                ExtendedStats = new Dictionary<string, object>
                {
                    ["indexName"] = _options.IndexName,
                    ["keyPrefix"] = _options.KeyPrefix,
                    ["algorithm"] = _options.Algorithm.ToString()
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

            await ExecuteCommandWithResultAsync(new[] { "PING" }, cts.Token);
            return true;
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
}
