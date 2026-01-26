using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.GrafanaLoki
{
    /// <summary>
    /// HTTP client for pushing logs to Grafana Loki via the Push API.
    /// Implements Loki Push API: POST /loki/api/v1/push
    /// </summary>
    public sealed class LokiClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly LokiConfiguration _config;
        private readonly JsonSerializerOptions _jsonOptions;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="LokiClient"/> class.
        /// </summary>
        /// <param name="config">The Loki configuration.</param>
        public LokiClient(LokiConfiguration config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds),
                BaseAddress = new Uri(_config.LokiUrl)
            };

            // Configure default headers
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            // Add tenant ID header if configured (for multi-tenancy)
            if (!string.IsNullOrEmpty(_config.TenantId))
            {
                _httpClient.DefaultRequestHeaders.Add("X-Scope-OrgID", _config.TenantId);
            }

            // Add basic authentication if configured
            if (!string.IsNullOrEmpty(_config.BasicAuthUsername) &&
                !string.IsNullOrEmpty(_config.BasicAuthPassword))
            {
                var credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{_config.BasicAuthUsername}:{_config.BasicAuthPassword}"));
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", credentials);
            }

            // Add custom headers if configured
            if (_config.CustomHeaders != null)
            {
                foreach (var header in _config.CustomHeaders)
                {
                    _httpClient.DefaultRequestHeaders.Add(header.Key, header.Value);
                }
            }

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
        }

        /// <summary>
        /// Pushes a batch of log streams to Loki.
        /// </summary>
        /// <param name="request">The push request containing streams.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if successful; otherwise, false.</returns>
        public async Task<bool> PushAsync(LokiPushRequest request, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (request == null || request.Streams.Count == 0)
            {
                return true; // Nothing to push
            }

            var attempt = 0;
            var delay = _config.RetryDelayMs;

            while (attempt <= _config.MaxRetries)
            {
                try
                {
                    var result = await PushInternalAsync(request, cancellationToken);
                    return result;
                }
                catch (HttpRequestException ex) when (attempt < _config.MaxRetries)
                {
                    // Retry on network errors
                    attempt++;
                    Console.Error.WriteLine(
                        $"[LokiClient] Push failed (attempt {attempt}/{_config.MaxRetries}): {ex.Message}");

                    await Task.Delay(delay, cancellationToken);
                    delay *= 2; // Exponential backoff
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Timeout - retry
                    attempt++;
                    if (attempt <= _config.MaxRetries)
                    {
                        Console.Error.WriteLine(
                            $"[LokiClient] Push timeout (attempt {attempt}/{_config.MaxRetries})");

                        await Task.Delay(delay, cancellationToken);
                        delay *= 2;
                    }
                    else
                    {
                        throw;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Internal push implementation without retry logic.
        /// </summary>
        private async Task<bool> PushInternalAsync(LokiPushRequest request, CancellationToken cancellationToken)
        {
            // Serialize the request to JSON
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));

            // Apply compression if enabled
            if (_config.EnableCompression)
            {
                var compressedContent = await CompressContentAsync(content);
                compressedContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                compressedContent.Headers.ContentEncoding.Add("gzip");
                content = compressedContent;
            }
            else
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            // Send the request to Loki Push API
            var response = await _httpClient.PostAsync("/loki/api/v1/push", content, cancellationToken);

            // Check response status
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            // Log error response
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.Error.WriteLine(
                $"[LokiClient] Push failed with status {response.StatusCode}: {responseBody}");

            // Determine if we should retry based on status code
            return response.StatusCode switch
            {
                HttpStatusCode.BadRequest => false, // Don't retry on client errors
                HttpStatusCode.Unauthorized => false,
                HttpStatusCode.Forbidden => false,
                HttpStatusCode.RequestEntityTooLarge => false,
                HttpStatusCode.TooManyRequests => throw new HttpRequestException("Rate limited"),
                _ => throw new HttpRequestException($"Loki returned {response.StatusCode}")
            };
        }

        /// <summary>
        /// Compresses content using GZIP.
        /// </summary>
        private static async Task<ByteArrayContent> CompressContentAsync(ByteArrayContent content)
        {
            var data = await content.ReadAsByteArrayAsync();

            using var outputStream = new MemoryStream();
            using (var gzipStream = new GZipStream(outputStream, CompressionLevel.Fastest))
            {
                await gzipStream.WriteAsync(data);
            }

            return new ByteArrayContent(outputStream.ToArray());
        }

        /// <summary>
        /// Tests the connection to Loki by sending a health check request.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if Loki is reachable; otherwise, false.</returns>
        public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            try
            {
                var response = await _httpClient.GetAsync("/ready", cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"[LokiClient] Health check failed: {ex.Message}");
                return false;
            }
            catch (TaskCanceledException)
            {
                Console.Error.WriteLine("[LokiClient] Health check timeout");
                return false;
            }
        }

        /// <summary>
        /// Disposes the HTTP client.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _httpClient.Dispose();
        }
    }
}
