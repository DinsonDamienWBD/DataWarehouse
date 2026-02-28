using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.TimeSeries;

/// <summary>
/// VictoriaMetrics time-series storage strategy with production-ready features:
/// - High-performance time-series database
/// - Prometheus-compatible API
/// - PromQL and MetricsQL support
/// - Excellent compression
/// - High cardinality handling
/// - Multi-tenancy
/// - Downsampling
/// </summary>
public sealed class VictoriaMetricsStorageStrategy : DatabaseStorageStrategyBase
{
    private HttpClient? _httpClient;
    private string _metricName = "storage_object";

    public override string StrategyId => "victoriametrics";
    public override string Name => "VictoriaMetrics Time-Series Storage";
    public override StorageTier Tier => StorageTier.Warm;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.TimeSeries;
    public override string Engine => "VictoriaMetrics";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = false,
        SupportsVersioning = false,
        SupportsTiering = true,
        SupportsEncryption = false,
        SupportsCompression = true,
        SupportsMultipart = false,
        MaxObjectSize = 1L * 1024 * 1024, // 1MB practical limit for labels
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Eventual
    };

    public override bool SupportsTransactions => false;
    public override bool SupportsSql => false;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _metricName = GetConfiguration("MetricName", "storage_object");

        var connectionString = GetConnectionString();
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(connectionString),
            Timeout = TimeSpan.FromSeconds(30)
        };

        await Task.CompletedTask;
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        var response = await _httpClient!.GetAsync("/health", ct);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        _httpClient?.Dispose();
        _httpClient = null;
        await Task.CompletedTask;
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var dataBase64 = Convert.ToBase64String(data);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : "";

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Store size metric via Prometheus exposition format (correct endpoint)
        var sizeLines = new StringBuilder();
        sizeLines.AppendLine($"{_metricName}_size{{key=\"{EscapeLabel(key)}\",etag=\"{EscapeLabel(etag)}\",content_type=\"{EscapeLabel(contentType ?? "")}\"}} {data.LongLength} {timestamp}");

        var sizeContent = new StringContent(sizeLines.ToString(), Encoding.UTF8, "text/plain");
        var sizeResponse = await _httpClient!.PostAsync("/api/v1/import/prometheus", sizeContent, ct);
        sizeResponse.EnsureSuccessStatusCode();

        // Store blob data via JSON Lines import format (correct VictoriaMetrics /api/v1/import format)
        // Each chunk gets its own metric with a chunk index label to support large blobs
        const int chunkSize = 16384; // 16KB chunks to stay within label size limits
        var chunks = SplitBase64IntoChunks(dataBase64, chunkSize);

        var jsonLines = new StringBuilder();
        for (int i = 0; i < chunks.Count; i++)
        {
            var entry = new
            {
                metric = new Dictionary<string, string>
                {
                    ["__name__"] = $"{_metricName}_blob",
                    ["key"] = key,
                    ["chunk"] = i.ToString(),
                    ["total_chunks"] = chunks.Count.ToString(),
                    ["data"] = chunks[i]
                },
                values = new[] { 1.0 },
                timestamps = new[] { timestamp }
            };
            jsonLines.AppendLine(JsonSerializer.Serialize(entry));
        }

        var blobContent = new StringContent(jsonLines.ToString(), Encoding.UTF8, "application/json");
        var blobResponse = await _httpClient.PostAsync("/api/v1/import", blobContent, ct);
        blobResponse.EnsureSuccessStatusCode();

        return new StorageObjectMetadata
        {
            Key = key,
            Size = data.LongLength,
            Created = now,
            Modified = now,
            ETag = etag,
            ContentType = contentType,
            CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
            Tier = Tier
        };
    }

    private static List<string> SplitBase64IntoChunks(string base64, int chunkSize)
    {
        var chunks = new List<string>();
        for (int i = 0; i < base64.Length; i += chunkSize)
        {
            chunks.Add(base64.Substring(i, Math.Min(chunkSize, base64.Length - i)));
        }
        if (chunks.Count == 0) chunks.Add("");
        return chunks;
    }

    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct)
    {
        // Query all blob chunks for this key, ordered by chunk index
        var query = Uri.EscapeDataString($"{_metricName}_blob{{key=\"{EscapeLabel(key)}\"}}");
        var response = await _httpClient!.GetAsync($"/api/v1/query?query={query}", ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var result = await response.Content.ReadFromJsonAsync<VmQueryResult>(cancellationToken: ct);
        var results = result?.Data?.Result;

        if (results == null || results.Length == 0)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        // Reassemble chunks in order
        var orderedChunks = results
            .Where(r => r.Metric != null && r.Metric.ContainsKey("data") && r.Metric.ContainsKey("chunk"))
            .OrderBy(r => int.TryParse(r.Metric!["chunk"], out var idx) ? idx : 0)
            .Select(r => r.Metric!["data"])
            .ToList();

        if (orderedChunks.Count == 0)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var fullBase64 = string.Concat(orderedChunks);
        return Convert.FromBase64String(fullBase64);
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        // VictoriaMetrics uses retention policies, not explicit deletes
        // You can use the delete API if enabled
        var query = Uri.EscapeDataString($"{{key=\"{key}\"}}");
        await _httpClient!.PostAsync($"/api/v1/admin/tsdb/delete_series?match[]={query}", null, ct);

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var query = Uri.EscapeDataString($"{_metricName}_size{{key=\"{key}\"}}");
        var response = await _httpClient!.GetAsync($"/api/v1/query?query={query}", ct);

        if (!response.IsSuccessStatusCode) return false;

        var result = await response.Content.ReadFromJsonAsync<VmQueryResult>(cancellationToken: ct);
        return result?.Data?.Result?.Any() == true;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var labelFilter = string.IsNullOrEmpty(prefix) ? "" : $",key=~\"^{prefix}.*\"";
        var query = Uri.EscapeDataString($"{_metricName}_size{{{labelFilter.TrimStart(',')}}}");

        var response = await _httpClient!.GetAsync($"/api/v1/query?query={query}", ct);
        if (!response.IsSuccessStatusCode) yield break;

        var result = await response.Content.ReadFromJsonAsync<VmQueryResult>(cancellationToken: ct);

        foreach (var item in result?.Data?.Result ?? Enumerable.Empty<VmResult>())
        {
            ct.ThrowIfCancellationRequested();

            if (item.Metric == null) continue;
            if (!item.Metric.TryGetValue("key", out var key)) continue;

            long.TryParse(item.Value?.LastOrDefault()?.ToString(), out var size);

            yield return new StorageObjectMetadata
            {
                Key = key,
                Size = size,
                ETag = item.Metric.GetValueOrDefault("etag"),
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                Tier = Tier
            };
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        var query = Uri.EscapeDataString($"{_metricName}_size{{key=\"{key}\"}}");
        var response = await _httpClient!.GetAsync($"/api/v1/query?query={query}", ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var result = await response.Content.ReadFromJsonAsync<VmQueryResult>(cancellationToken: ct);
        var data = result?.Data?.Result?.FirstOrDefault();

        if (data?.Metric == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        long.TryParse(data.Value?.LastOrDefault()?.ToString(), out var size);

        return new StorageObjectMetadata
        {
            Key = key,
            Size = size,
            ETag = data.Metric.GetValueOrDefault("etag"),
            ContentType = data.Metric.GetValueOrDefault("contentType"),
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow,
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            var response = await _httpClient!.GetAsync("/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeLabel(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _httpClient?.Dispose();
        await base.DisposeAsyncCore();
    }

    private sealed class VmQueryResult
    {
        [JsonPropertyName("data")]
        public VmData? Data { get; set; }
    }

    private sealed class VmData
    {
        [JsonPropertyName("result")]
        public VmResult[]? Result { get; set; }
    }

    private sealed class VmResult
    {
        [JsonPropertyName("metric")]
        public Dictionary<string, string>? Metric { get; set; }

        [JsonPropertyName("value")]
        public object[]? Value { get; set; }
    }
}
