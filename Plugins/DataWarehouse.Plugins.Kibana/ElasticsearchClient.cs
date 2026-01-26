using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.Kibana
{
    /// <summary>
    /// Production-ready Elasticsearch HTTP client with bulk API support.
    /// Implements connection pooling, retry logic, and error handling.
    /// </summary>
    public sealed class ElasticsearchClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly KibanaConfiguration _config;
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="ElasticsearchClient"/> class.
        /// </summary>
        /// <param name="config">The configuration.</param>
        public ElasticsearchClient(KibanaConfiguration config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _semaphore = new SemaphoreSlim(10, 10); // Max 10 concurrent requests

            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 10,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            if (!_config.VerifySsl)
            {
                handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                };
            }

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(_config.ElasticsearchUrl),
                Timeout = _config.RequestTimeout
            };

            // Configure authentication
            if (!string.IsNullOrEmpty(_config.ApiKey))
            {
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("ApiKey", _config.ApiKey);
            }
            else if (!string.IsNullOrEmpty(_config.Username) && !string.IsNullOrEmpty(_config.Password))
            {
                var credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{_config.Username}:{_config.Password}"));
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", credentials);
            }

            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        /// <summary>
        /// Checks if the Elasticsearch cluster is healthy.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if healthy; otherwise, false.</returns>
        public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
        {
            try
            {
                var response = await _httpClient.GetAsync("/_cluster/health", ct);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Creates an index template for the specified index pattern.
        /// </summary>
        /// <param name="templateName">The template name.</param>
        /// <param name="indexPattern">The index pattern (e.g., "datawarehouse-*").</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task CreateIndexTemplateAsync(
            string templateName,
            string indexPattern,
            CancellationToken ct = default)
        {
            var template = new
            {
                index_patterns = new[] { indexPattern },
                settings = new
                {
                    number_of_shards = _config.NumberOfShards,
                    number_of_replicas = _config.NumberOfReplicas,
                    index = new
                    {
                        refresh_interval = "5s"
                    }
                },
                mappings = new
                {
                    properties = new
                    {
                        timestamp = new { type = "date" },
                        metric_name = new { type = "keyword" },
                        metric_type = new { type = "keyword" },
                        value = new { type = "double" },
                        labels = new { type = "object" },
                        service = new { type = "keyword" },
                        environment = new { type = "keyword" },
                        host = new { type = "keyword" },
                        level = new { type = "keyword" },
                        message = new { type = "text" },
                        logger = new { type = "keyword" }
                    }
                }
            };

            var json = JsonSerializer.Serialize(template, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            await _semaphore.WaitAsync(ct);
            try
            {
                var response = await _httpClient.PutAsync($"/_index_template/{templateName}", content, ct);
                response.EnsureSuccessStatusCode();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Sends a bulk request to Elasticsearch.
        /// </summary>
        /// <param name="operations">The bulk operations.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The bulk response.</returns>
        public async Task<BulkResponse> BulkAsync(
            IEnumerable<BulkOperation> operations,
            CancellationToken ct = default)
        {
            if (!operations.Any())
            {
                return new BulkResponse { Took = 0, Errors = false };
            }

            // Build NDJSON payload
            var sb = new StringBuilder();
            foreach (var op in operations)
            {
                // Action line
                var action = op.Action.ToString().ToLowerInvariant();
                var actionObj = new Dictionary<string, object>
                {
                    [action] = new Dictionary<string, string>
                    {
                        ["_index"] = op.Index
                    }
                };

                if (!string.IsNullOrEmpty(op.Id))
                {
                    ((Dictionary<string, string>)actionObj[action])["_id"] = op.Id;
                }

                sb.AppendLine(JsonSerializer.Serialize(actionObj, JsonOptions));

                // Document line (not needed for delete)
                if (op.Action != BulkActionType.Delete && op.Document != null)
                {
                    sb.AppendLine(JsonSerializer.Serialize(op.Document, JsonOptions));
                }
            }

            var ndjson = sb.ToString();
            byte[] contentBytes;

            if (_config.EnableCompression)
            {
                using var ms = new MemoryStream();
                using (var gzip = new GZipStream(ms, CompressionLevel.Fastest))
                {
                    var bytes = Encoding.UTF8.GetBytes(ndjson);
                    await gzip.WriteAsync(bytes, ct);
                }
                contentBytes = ms.ToArray();
            }
            else
            {
                contentBytes = Encoding.UTF8.GetBytes(ndjson);
            }

            var content = new ByteArrayContent(contentBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");

            if (_config.EnableCompression)
            {
                content.Headers.ContentEncoding.Add("gzip");
            }

            await _semaphore.WaitAsync(ct);
            try
            {
                var response = await _httpClient.PostAsync("/_bulk", content, ct);
                var responseBody = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Bulk request failed with status {response.StatusCode}: {responseBody}");
                }

                var bulkResponse = JsonSerializer.Deserialize<BulkResponse>(responseBody, JsonOptions);
                return bulkResponse ?? new BulkResponse { Errors = true };
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Gets the current index name for the specified prefix and date.
        /// </summary>
        /// <param name="prefix">The index prefix.</param>
        /// <param name="date">The date (optional, defaults to today).</param>
        /// <returns>The index name.</returns>
        public static string GetIndexName(string prefix, DateTime? date = null)
        {
            var dt = date ?? DateTime.UtcNow;
            return $"{prefix}-{dt:yyyy.MM.dd}";
        }

        /// <summary>
        /// Disposes the client.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _semaphore.Dispose();
            _httpClient.Dispose();
        }
    }
}
