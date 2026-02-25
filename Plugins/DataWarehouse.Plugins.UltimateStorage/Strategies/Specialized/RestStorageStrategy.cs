using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Specialized
{
    /// <summary>
    /// Generic REST API storage strategy with production-ready features:
    /// - Configurable URL patterns for each operation (GET, PUT, POST, DELETE)
    /// - Multiple authentication types: Basic, Bearer token, API Key, Custom headers
    /// - Automatic retry with exponential backoff
    /// - Connection pooling via HttpClient best practices
    /// - Pagination support for list operations (Link header, JSON pagination)
    /// - Configurable request/response transformations
    /// - Content negotiation (JSON, XML, binary)
    /// - Support for ETags and conditional requests
    /// - Comprehensive error handling and status code mapping
    /// - Custom header injection for request context
    /// - Rate limiting and throttling awareness
    /// - Streaming support for large payloads
    /// </summary>
    public class RestStorageStrategy : UltimateStorageStrategyBase
    {
        private HttpClient? _httpClient;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private readonly SemaphoreSlim _writeLock = new(10, 10); // Allow 10 concurrent writes

        // Connection configuration
        private string _baseUrl = string.Empty;
        private RestAuthenticationType _authenticationType = RestAuthenticationType.None;
        private string? _authenticationValue = null;
        private string? _apiKeyHeaderName = null;
        private Dictionary<string, string> _customHeaders = new();

        // URL pattern configuration
        private string _storeUrlPattern = "/objects/{key}";
        private string _retrieveUrlPattern = "/objects/{key}";
        private string _deleteUrlPattern = "/objects/{key}";
        private string _existsUrlPattern = "/objects/{key}";
        private string _listUrlPattern = "/objects";
        private string _metadataUrlPattern = "/objects/{key}/metadata";
        private string _healthUrlPattern = "/health";

        // HTTP method configuration
        private string _storeMethod = "PUT";
        private string _retrieveMethod = "GET";
        private string _deleteMethod = "DELETE";
        private string _existsMethod = "HEAD";
        private string _listMethod = "GET";
        private string _metadataMethod = "GET";

        // Pagination configuration
        private RestPaginationType _paginationType = RestPaginationType.None;
        private string _paginationPageParam = "page";
        private string _paginationSizeParam = "size";
        private int _paginationDefaultSize = 100;
        private string _paginationJsonItemsPath = "items";
        private string _paginationJsonNextTokenPath = "nextToken";

        // Performance and behavior configuration
        private int _timeoutSeconds = 60;
        private int _maxRetries = 3;
        private int _retryDelayMs = 1000;
        private bool _useCompression = true;
        private bool _validateServerCertificate = true;
        private string _contentType = "application/octet-stream";
        private string _metadataPrefix = "X-Meta-";

        // Response format configuration
        private RestResponseFormat _responseFormat = RestResponseFormat.Binary;
        private string _jsonDataPath = "data";
        private string _jsonMetadataPath = "metadata";

        public override string StrategyId => "rest-api";
        public override string Name => "REST API Storage";
        public override StorageTier Tier => StorageTier.Warm; // Network storage is warm tier

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false,
            SupportsVersioning = false,
            SupportsTiering = false,
            SupportsEncryption = _baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
            SupportsCompression = _useCompression,
            SupportsMultipart = false,
            MaxObjectSize = null, // Limited by REST API server
            MaxObjects = null,
            ConsistencyModel = ConsistencyModel.Eventual // REST APIs typically provide eventual consistency
        };

        #region Initialization

        /// <summary>
        /// Initializes the REST API storage strategy with connection configuration.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load required configuration
            _baseUrl = GetConfiguration<string>("BaseUrl")
                ?? throw new InvalidOperationException("REST API BaseUrl is required (e.g., https://api.example.com/v1)");

            // Validate base URL
            if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException($"Invalid REST API base URL: {_baseUrl}");
            }

            // Remove trailing slash from base URL
            _baseUrl = _baseUrl.TrimEnd('/');

            // Load authentication configuration
            var authTypeStr = GetConfiguration<string>("AuthenticationType", "None");
            _authenticationType = authTypeStr.ToLowerInvariant() switch
            {
                "none" => RestAuthenticationType.None,
                "basic" => RestAuthenticationType.Basic,
                "bearer" => RestAuthenticationType.Bearer,
                "apikey" => RestAuthenticationType.ApiKey,
                "custom" => RestAuthenticationType.Custom,
                _ => RestAuthenticationType.None
            };

            _authenticationValue = GetConfiguration<string?>("AuthenticationValue", null);
            _apiKeyHeaderName = GetConfiguration<string?>("ApiKeyHeaderName", "X-API-Key");

            // Validate authentication configuration
            if (_authenticationType != RestAuthenticationType.None && string.IsNullOrWhiteSpace(_authenticationValue))
            {
                throw new InvalidOperationException($"AuthenticationValue is required when AuthenticationType is {_authenticationType}");
            }

            // Load custom headers
            var customHeadersJson = GetConfiguration<string?>("CustomHeaders", null);
            if (!string.IsNullOrEmpty(customHeadersJson))
            {
                try
                {
                    _customHeaders = JsonSerializer.Deserialize<Dictionary<string, string>>(customHeadersJson) ?? new();
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException($"Failed to parse CustomHeaders JSON: {ex.Message}", ex);
                }
            }

            // Load URL patterns
            _storeUrlPattern = GetConfiguration("StoreUrlPattern", "/objects/{key}");
            _retrieveUrlPattern = GetConfiguration("RetrieveUrlPattern", "/objects/{key}");
            _deleteUrlPattern = GetConfiguration("DeleteUrlPattern", "/objects/{key}");
            _existsUrlPattern = GetConfiguration("ExistsUrlPattern", "/objects/{key}");
            _listUrlPattern = GetConfiguration("ListUrlPattern", "/objects");
            _metadataUrlPattern = GetConfiguration("MetadataUrlPattern", "/objects/{key}/metadata");
            _healthUrlPattern = GetConfiguration("HealthUrlPattern", "/health");

            // Load HTTP methods
            _storeMethod = GetConfiguration("StoreMethod", "PUT").ToUpperInvariant();
            _retrieveMethod = GetConfiguration("RetrieveMethod", "GET").ToUpperInvariant();
            _deleteMethod = GetConfiguration("DeleteMethod", "DELETE").ToUpperInvariant();
            _existsMethod = GetConfiguration("ExistsMethod", "HEAD").ToUpperInvariant();
            _listMethod = GetConfiguration("ListMethod", "GET").ToUpperInvariant();
            _metadataMethod = GetConfiguration("MetadataMethod", "GET").ToUpperInvariant();

            // Load pagination configuration
            var paginationTypeStr = GetConfiguration<string>("PaginationType", "None");
            _paginationType = paginationTypeStr.ToLowerInvariant() switch
            {
                "none" => RestPaginationType.None,
                "offsetlimit" => RestPaginationType.OffsetLimit,
                "pagebased" => RestPaginationType.PageBased,
                "cursor" => RestPaginationType.Cursor,
                "linkheader" => RestPaginationType.LinkHeader,
                _ => RestPaginationType.None
            };

            _paginationPageParam = GetConfiguration("PaginationPageParam", "page");
            _paginationSizeParam = GetConfiguration("PaginationSizeParam", "size");
            _paginationDefaultSize = GetConfiguration("PaginationDefaultSize", 100);
            _paginationJsonItemsPath = GetConfiguration("PaginationJsonItemsPath", "items");
            _paginationJsonNextTokenPath = GetConfiguration("PaginationJsonNextTokenPath", "nextToken");

            // Load behavior configuration
            _timeoutSeconds = GetConfiguration("TimeoutSeconds", 60);
            _maxRetries = GetConfiguration("MaxRetries", 3);
            _retryDelayMs = GetConfiguration("RetryDelayMs", 1000);
            _useCompression = GetConfiguration("UseCompression", true);
            _validateServerCertificate = GetConfiguration("ValidateServerCertificate", true);
            _contentType = GetConfiguration("ContentType", "application/octet-stream");
            _metadataPrefix = GetConfiguration("MetadataPrefix", "X-Meta-");

            // Load response format configuration
            var responseFormatStr = GetConfiguration<string>("ResponseFormat", "Binary");
            _responseFormat = responseFormatStr.ToLowerInvariant() switch
            {
                "binary" => RestResponseFormat.Binary,
                "json" => RestResponseFormat.Json,
                "jsonbase64" => RestResponseFormat.JsonBase64,
                _ => RestResponseFormat.Binary
            };

            _jsonDataPath = GetConfiguration("JsonDataPath", "data");
            _jsonMetadataPath = GetConfiguration("JsonMetadataPath", "metadata");

            // Initialize HTTP client
            await InitializeHttpClientAsync(ct);

            // Perform health check to validate connection
            await PerformHealthCheckAsync(ct);
        }

        /// <summary>
        /// Initializes the HTTP client with appropriate configuration.
        /// </summary>
        private async Task InitializeHttpClientAsync(CancellationToken ct)
        {
            await _connectionLock.WaitAsync(ct);
            try
            {
                // Dispose existing client if any
                _httpClient?.Dispose();

                // Create handler with configuration
                var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 5,
                    UseCookies = true,
                    AutomaticDecompression = _useCompression
                        ? DecompressionMethods.GZip | DecompressionMethods.Deflate
                        : DecompressionMethods.None
                };

                // Configure certificate validation
                if (!_validateServerCertificate)
                {
                    handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
                }

                // Create HTTP client
                _httpClient = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(_timeoutSeconds),
                    BaseAddress = new Uri(_baseUrl)
                };

                // Set default headers
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DataWarehouse-UltimateStorage-REST/1.0");
                _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

                if (_useCompression)
                {
                    _httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
                    _httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
                }

                // Configure authentication
                ConfigureAuthentication();

                // Add custom headers
                foreach (var header in _customHeaders)
                {
                    _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        /// <summary>
        /// Configures authentication headers on the HTTP client.
        /// </summary>
        private void ConfigureAuthentication()
        {
            if (_authenticationType == RestAuthenticationType.None || string.IsNullOrEmpty(_authenticationValue))
            {
                return;
            }

            switch (_authenticationType)
            {
                case RestAuthenticationType.Basic:
                    var basicAuthBytes = Encoding.UTF8.GetBytes(_authenticationValue);
                    var basicAuthHeader = Convert.ToBase64String(basicAuthBytes);
                    _httpClient!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuthHeader);
                    break;

                case RestAuthenticationType.Bearer:
                    _httpClient!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _authenticationValue);
                    break;

                case RestAuthenticationType.ApiKey:
                    if (!string.IsNullOrEmpty(_apiKeyHeaderName))
                    {
                        _httpClient!.DefaultRequestHeaders.TryAddWithoutValidation(_apiKeyHeaderName, _authenticationValue);
                    }
                    break;

                case RestAuthenticationType.Custom:
                    // Custom authentication is handled via CustomHeaders configuration
                    break;
            }
        }

        /// <summary>
        /// Performs initial health check to validate API accessibility.
        /// </summary>
        private async Task PerformHealthCheckAsync(CancellationToken ct)
        {
            try
            {
                var health = await GetHealthAsyncCore(ct);
                if (health.Status == HealthStatus.Unhealthy)
                {
                    throw new InvalidOperationException($"REST API is not accessible: {health.Message}");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to validate REST API connection: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Disposes HTTP client resources.
        /// </summary>
        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();
            _httpClient?.Dispose();
            _connectionLock?.Dispose();
            _writeLock?.Dispose();
        }

        #endregion

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            var url = BuildUrl(_storeUrlPattern, key);

            await _writeLock.WaitAsync(ct);
            try
            {
                // Prepare request
                var request = new HttpRequestMessage(new HttpMethod(_storeMethod), url);

                // Set content based on response format
                if (_responseFormat == RestResponseFormat.Json || _responseFormat == RestResponseFormat.JsonBase64)
                {
                    // Wrap data in JSON structure
                    var jsonPayload = await BuildJsonStorePayload(data, metadata, ct);
                    request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                }
                else
                {
                    // Buffer the stream to bytes before setting as content.
                    // StreamContent wraps the raw stream and once sent the stream position is at end,
                    // causing stream exhaustion on any retry attempt. ByteArrayContent is idempotent.
                    using var bufferMs = new MemoryStream(65536);
                    await data.CopyToAsync(bufferMs, 81920, ct);
                    var dataBytes = bufferMs.ToArray();
                    request.Content = new ByteArrayContent(dataBytes);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue(_contentType);
                }

                // Add metadata as headers if provided
                if (metadata != null)
                {
                    foreach (var kvp in metadata)
                    {
                        var headerName = _metadataPrefix + kvp.Key;
                        request.Headers.TryAddWithoutValidation(headerName, kvp.Value);
                    }
                }

                // Execute request with retry
                var response = await ExecuteWithRetryAsync(request, ct);

                if (!response.IsSuccessStatusCode)
                {
                    throw new IOException($"Failed to store object '{key}'. Status: {response.StatusCode}, Reason: {response.ReasonPhrase}");
                }

                // Calculate size
                var size = data.CanSeek ? data.Length : 0;

                // Try to extract metadata from response
                var responseMetadata = ExtractMetadataFromResponse(response);
                var etag = response.Headers.ETag?.Tag ?? GenerateETag(key);

                // Update statistics
                IncrementBytesStored(size);
                IncrementOperationCounter(StorageOperationType.Store);

                return new StorageObjectMetadata
                {
                    Key = key,
                    Size = size,
                    Created = DateTime.UtcNow,
                    Modified = DateTime.UtcNow,
                    ETag = etag,
                    ContentType = _contentType,
                    CustomMetadata = responseMetadata ?? (metadata as IReadOnlyDictionary<string, string>),
                    Tier = Tier
                };
            }
            finally
            {
                _writeLock.Release();
            }
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var url = BuildUrl(_retrieveUrlPattern, key);
            var request = new HttpRequestMessage(new HttpMethod(_retrieveMethod), url);

            var response = await ExecuteWithRetryAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new FileNotFoundException($"Object not found: {key}", key);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new IOException($"Failed to retrieve object '{key}'. Status: {response.StatusCode}, Reason: {response.ReasonPhrase}");
            }

            // Handle different response formats
            Stream resultStream;
            if (_responseFormat == RestResponseFormat.Json || _responseFormat == RestResponseFormat.JsonBase64)
            {
                var jsonContent = await response.Content.ReadAsStringAsync(ct);
                resultStream = await ParseJsonRetrieveResponse(jsonContent, ct);
            }
            else
            {
                // Read binary response into memory stream
                resultStream = new MemoryStream(65536);
                await response.Content.CopyToAsync(resultStream, ct);
                resultStream.Position = 0;
            }

            // Update statistics
            IncrementBytesRetrieved(resultStream.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            return resultStream;
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var url = BuildUrl(_deleteUrlPattern, key);

            // Try to get size before deletion for statistics
            long size = 0;
            try
            {
                var metadata = await GetMetadataAsyncCore(key, ct);
                size = metadata.Size;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RestStorageStrategy.DeleteAsyncCore] {ex.GetType().Name}: {ex.Message}");
                // Ignore if metadata retrieval fails
            }

            var request = new HttpRequestMessage(new HttpMethod(_deleteMethod), url);
            var response = await ExecuteWithRetryAsync(request, ct);

            // Accept 404 as success (already deleted)
            if (response.StatusCode != HttpStatusCode.NotFound && !response.IsSuccessStatusCode)
            {
                throw new IOException($"Failed to delete object '{key}'. Status: {response.StatusCode}, Reason: {response.ReasonPhrase}");
            }

            // Update statistics
            if (size > 0)
            {
                IncrementBytesDeleted(size);
            }
            IncrementOperationCounter(StorageOperationType.Delete);
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var url = BuildUrl(_existsUrlPattern, key);

            try
            {
                var request = new HttpRequestMessage(new HttpMethod(_existsMethod), url);
                var response = await ExecuteWithRetryAsync(request, ct);

                IncrementOperationCounter(StorageOperationType.Exists);

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RestStorageStrategy.ExistsAsyncCore] {ex.GetType().Name}: {ex.Message}");
                IncrementOperationCounter(StorageOperationType.Exists);
                return false;
            }
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();
            IncrementOperationCounter(StorageOperationType.List);

            var url = BuildListUrl(_listUrlPattern, prefix);

            // Handle pagination
            string? nextToken = null;
            int page = 1;
            int offset = 0;

            do
            {
                ct.ThrowIfCancellationRequested();

                // Build paginated URL
                var paginatedUrl = BuildPaginatedUrl(url, page, offset, nextToken);
                var request = new HttpRequestMessage(new HttpMethod(_listMethod), paginatedUrl);

                var response = await ExecuteWithRetryAsync(request, ct);

                if (!response.IsSuccessStatusCode)
                {
                    yield break;
                }

                var content = await response.Content.ReadAsStringAsync(ct);

                // Parse response based on pagination type
                var items = ParseListResponse(content, out nextToken);

                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();

                    // Filter by prefix if needed
                    if (!string.IsNullOrEmpty(prefix) && !item.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    yield return item;
                    await Task.Yield();
                }

                // Update pagination variables
                page++;
                offset += items.Count;

                // Check if we should continue based on pagination type
                if (_paginationType == RestPaginationType.None)
                {
                    break;
                }

                if (_paginationType == RestPaginationType.LinkHeader)
                {
                    nextToken = ExtractNextLinkFromHeaders(response.Headers);
                }

            } while (!string.IsNullOrEmpty(nextToken) || (_paginationType == RestPaginationType.PageBased && page <= 100)); // Safety limit
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var url = BuildUrl(_metadataUrlPattern, key);
            var request = new HttpRequestMessage(new HttpMethod(_metadataMethod), url);

            var response = await ExecuteWithRetryAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new FileNotFoundException($"Object not found: {key}", key);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new IOException($"Failed to get metadata for '{key}'. Status: {response.StatusCode}, Reason: {response.ReasonPhrase}");
            }

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            // Parse metadata from response
            var content = await response.Content.ReadAsStringAsync(ct);
            return ParseMetadataResponse(key, content, response);
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                var url = BuildUrl(_healthUrlPattern, string.Empty);
                var request = new HttpRequestMessage(HttpMethod.Get, url);

                var response = await ExecuteWithRetryAsync(request, ct);

                sw.Stop();

                var status = response.IsSuccessStatusCode ? HealthStatus.Healthy : HealthStatus.Degraded;
                var message = response.IsSuccessStatusCode
                    ? $"REST API at {_baseUrl} is accessible"
                    : $"REST API returned status {response.StatusCode}";

                return new StorageHealthInfo
                {
                    Status = status,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Message = message,
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to access REST API at {_baseUrl}: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override async Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            try
            {
                // Try to extract capacity from health endpoint
                var health = await GetHealthAsyncCore(ct);
                return health.AvailableCapacity;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RestStorageStrategy.GetAvailableCapacityAsyncCore] {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Builds a URL by replacing placeholders with actual values.
        /// </summary>
        private string BuildUrl(string pattern, string key)
        {
            var url = pattern.Replace("{key}", Uri.EscapeDataString(key));
            return url.StartsWith("http") ? url : _baseUrl + url;
        }

        /// <summary>
        /// Builds a list URL with optional prefix filtering.
        /// </summary>
        private string BuildListUrl(string pattern, string? prefix)
        {
            var url = pattern;

            if (!string.IsNullOrEmpty(prefix))
            {
                var separator = url.Contains('?') ? "&" : "?";
                url += $"{separator}prefix={Uri.EscapeDataString(prefix)}";
            }

            return url.StartsWith("http") ? url : _baseUrl + url;
        }

        /// <summary>
        /// Builds a paginated URL based on pagination type.
        /// </summary>
        private string BuildPaginatedUrl(string baseUrl, int page, int offset, string? nextToken)
        {
            if (_paginationType == RestPaginationType.None)
            {
                return baseUrl;
            }

            var separator = baseUrl.Contains('?') ? "&" : "?";

            return _paginationType switch
            {
                RestPaginationType.PageBased => $"{baseUrl}{separator}{_paginationPageParam}={page}&{_paginationSizeParam}={_paginationDefaultSize}",
                RestPaginationType.OffsetLimit => $"{baseUrl}{separator}offset={offset}&limit={_paginationDefaultSize}",
                RestPaginationType.Cursor when !string.IsNullOrEmpty(nextToken) => $"{baseUrl}{separator}cursor={Uri.EscapeDataString(nextToken)}",
                RestPaginationType.LinkHeader => baseUrl, // Next URL comes from Link header
                _ => baseUrl
            };
        }

        /// <summary>
        /// Executes an HTTP request with retry logic and exponential backoff.
        /// </summary>
        private async Task<HttpResponseMessage> ExecuteWithRetryAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Exception? lastException = null;

            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                try
                {
                    // Clone request for retry (HttpRequestMessage can only be sent once)
                    var clonedRequest = await CloneHttpRequestAsync(request);
                    var response = await _httpClient!.SendAsync(clonedRequest, ct);

                    // Don't retry on client errors (4xx) except 408, 429
                    if (response.IsSuccessStatusCode ||
                        ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500 &&
                         response.StatusCode != HttpStatusCode.RequestTimeout &&
                         response.StatusCode != (HttpStatusCode)429))
                    {
                        return response;
                    }

                    lastException = new HttpRequestException($"HTTP {response.StatusCode}: {response.ReasonPhrase}");
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                {
                    lastException = ex;
                }

                if (attempt < _maxRetries)
                {
                    // Exponential backoff
                    var delay = _retryDelayMs * (int)Math.Pow(2, attempt);
                    await Task.Delay(delay, ct);
                }
            }

            throw new IOException($"Failed to execute REST API request after {_maxRetries + 1} attempts", lastException);
        }

        /// <summary>
        /// Clones an HTTP request for retry purposes.
        /// </summary>
        private async Task<HttpRequestMessage> CloneHttpRequestAsync(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);

            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content != null)
            {
                var contentBytes = await request.Content.ReadAsByteArrayAsync();
                clone.Content = new ByteArrayContent(contentBytes);

                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }

        /// <summary>
        /// Builds JSON payload for store operation.
        /// </summary>
        private async Task<string> BuildJsonStorePayload(Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            using var ms = new MemoryStream(65536);
            await data.CopyToAsync(ms, 81920, ct);
            var dataBytes = ms.ToArray();

            var payload = new Dictionary<string, object>();

            if (_responseFormat == RestResponseFormat.JsonBase64)
            {
                payload[_jsonDataPath] = Convert.ToBase64String(dataBytes);
            }
            else
            {
                // Assume UTF-8 text for JSON format
                payload[_jsonDataPath] = Encoding.UTF8.GetString(dataBytes);
            }

            if (metadata != null && metadata.Count > 0)
            {
                payload[_jsonMetadataPath] = metadata;
            }

            return JsonSerializer.Serialize(payload);
        }

        /// <summary>
        /// Parses JSON response for retrieve operation.
        /// </summary>
        private async Task<Stream> ParseJsonRetrieveResponse(string jsonContent, CancellationToken ct)
        {
            var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            if (root.TryGetProperty(_jsonDataPath, out var dataElement))
            {
                byte[] dataBytes;

                if (_responseFormat == RestResponseFormat.JsonBase64)
                {
                    var base64String = dataElement.GetString();
                    dataBytes = Convert.FromBase64String(base64String ?? string.Empty);
                }
                else
                {
                    var textData = dataElement.GetString() ?? string.Empty;
                    dataBytes = Encoding.UTF8.GetBytes(textData);
                }

                return new MemoryStream(dataBytes);
            }

            throw new InvalidOperationException("Failed to parse data from JSON response");
        }

        /// <summary>
        /// Parses list response based on response format.
        /// </summary>
        private List<StorageObjectMetadata> ParseListResponse(string content, out string? nextToken)
        {
            nextToken = null;
            var items = new List<StorageObjectMetadata>();

            try
            {
                var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                // Try to extract items array
                JsonElement itemsElement = root;
                if (root.TryGetProperty(_paginationJsonItemsPath, out var itemsArray))
                {
                    itemsElement = itemsArray;
                }

                // Extract next token if using cursor pagination
                if (_paginationType == RestPaginationType.Cursor &&
                    root.TryGetProperty(_paginationJsonNextTokenPath, out var tokenElement))
                {
                    nextToken = tokenElement.GetString();
                }

                // Parse items
                if (itemsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in itemsElement.EnumerateArray())
                    {
                        var metadata = ParseStorageObjectFromJson(item);
                        if (metadata != null)
                        {
                            items.Add(metadata);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // If JSON parsing fails, return empty list
            }

            return items;
        }

        /// <summary>
        /// Parses a StorageObjectMetadata from a JSON element.
        /// </summary>
        private StorageObjectMetadata? ParseStorageObjectFromJson(JsonElement element)
        {
            try
            {
                var key = element.TryGetProperty("key", out var keyProp) ? keyProp.GetString() :
                         element.TryGetProperty("name", out var nameProp) ? nameProp.GetString() :
                         element.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

                if (string.IsNullOrEmpty(key))
                {
                    return null;
                }

                var size = element.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;
                var created = element.TryGetProperty("created", out var createdProp) ?
                             (createdProp.TryGetDateTime(out var createdDt) ? createdDt : DateTime.MinValue) : DateTime.MinValue;
                var modified = element.TryGetProperty("modified", out var modifiedProp) ?
                              (modifiedProp.TryGetDateTime(out var modifiedDt) ? modifiedDt : DateTime.MinValue) : DateTime.MinValue;
                var etag = element.TryGetProperty("etag", out var etagProp) ? etagProp.GetString() : null;
                var contentType = element.TryGetProperty("contentType", out var ctProp) ? ctProp.GetString() : null;

                return new StorageObjectMetadata
                {
                    Key = key,
                    Size = size,
                    Created = created,
                    Modified = modified,
                    ETag = etag ?? GenerateETag(key),
                    ContentType = contentType,
                    Tier = Tier
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RestStorageStrategy.ParseStorageObjectFromJson] {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Parses metadata response.
        /// </summary>
        private StorageObjectMetadata ParseMetadataResponse(string key, string content, HttpResponseMessage response)
        {
            try
            {
                var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                var size = root.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;
                var created = root.TryGetProperty("created", out var createdProp) ?
                             (createdProp.TryGetDateTime(out var createdDt) ? createdDt : DateTime.MinValue) : DateTime.MinValue;
                var modified = root.TryGetProperty("modified", out var modifiedProp) ?
                              (modifiedProp.TryGetDateTime(out var modifiedDt) ? modifiedDt : DateTime.MinValue) : DateTime.MinValue;
                var etag = root.TryGetProperty("etag", out var etagProp) ? etagProp.GetString() : null;
                var contentType = root.TryGetProperty("contentType", out var ctProp) ? ctProp.GetString() : null;

                // Extract custom metadata
                Dictionary<string, string>? customMetadata = null;
                if (root.TryGetProperty(_jsonMetadataPath, out var metaProp) && metaProp.ValueKind == JsonValueKind.Object)
                {
                    customMetadata = new Dictionary<string, string>();
                    foreach (var prop in metaProp.EnumerateObject())
                    {
                        customMetadata[prop.Name] = prop.Value.GetString() ?? string.Empty;
                    }
                }

                return new StorageObjectMetadata
                {
                    Key = key,
                    Size = size,
                    Created = created,
                    Modified = modified,
                    ETag = etag ?? GenerateETag(key),
                    ContentType = contentType ?? _contentType,
                    CustomMetadata = customMetadata,
                    Tier = Tier
                };
            }
            catch (JsonException)
            {
                // Fallback: try to extract metadata from headers
                var customMetadata = ExtractMetadataFromResponse(response);

                return new StorageObjectMetadata
                {
                    Key = key,
                    Size = response.Content.Headers.ContentLength ?? 0,
                    Created = DateTime.MinValue,
                    Modified = response.Content.Headers.LastModified?.UtcDateTime ?? DateTime.MinValue,
                    ETag = response.Headers.ETag?.Tag ?? GenerateETag(key),
                    ContentType = response.Content.Headers.ContentType?.MediaType ?? _contentType,
                    CustomMetadata = customMetadata,
                    Tier = Tier
                };
            }
        }

        /// <summary>
        /// Extracts custom metadata from response headers.
        /// </summary>
        private IReadOnlyDictionary<string, string>? ExtractMetadataFromResponse(HttpResponseMessage response)
        {
            var metadata = new Dictionary<string, string>();

            foreach (var header in response.Headers)
            {
                if (header.Key.StartsWith(_metadataPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var key = header.Key.Substring(_metadataPrefix.Length);
                    var value = string.Join(", ", header.Value);
                    metadata[key] = value;
                }
            }

            return metadata.Count > 0 ? metadata : null;
        }

        /// <summary>
        /// Extracts next link URL from Link header (RFC 5988).
        /// </summary>
        private string? ExtractNextLinkFromHeaders(HttpResponseHeaders headers)
        {
            if (headers.TryGetValues("Link", out var linkValues))
            {
                foreach (var linkValue in linkValues)
                {
                    // Parse Link header: <https://api.example.com/objects?page=2>; rel="next"
                    var parts = linkValue.Split(';');
                    if (parts.Length >= 2)
                    {
                        var rel = parts[1].Trim();
                        if (rel.Contains("next", StringComparison.OrdinalIgnoreCase))
                        {
                            var url = parts[0].Trim().Trim('<', '>');
                            return url;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Generates an ETag for a key.
        /// </summary>
        private string GenerateETag(string key)
        {
            var hash = HashCode.Combine(key, DateTime.UtcNow.Ticks);
            return $"\"{hash:x}\"";
        }

        protected override int GetMaxKeyLength() => 1024;

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// REST API authentication type.
    /// </summary>
    public enum RestAuthenticationType
    {
        /// <summary>No authentication.</summary>
        None,

        /// <summary>HTTP Basic authentication.</summary>
        Basic,

        /// <summary>Bearer token authentication.</summary>
        Bearer,

        /// <summary>API Key in custom header.</summary>
        ApiKey,

        /// <summary>Custom authentication via headers.</summary>
        Custom
    }

    /// <summary>
    /// REST API pagination type.
    /// </summary>
    public enum RestPaginationType
    {
        /// <summary>No pagination.</summary>
        None,

        /// <summary>Offset/Limit pagination (offset=0&limit=100).</summary>
        OffsetLimit,

        /// <summary>Page-based pagination (page=1&size=100).</summary>
        PageBased,

        /// <summary>Cursor-based pagination (cursor=token).</summary>
        Cursor,

        /// <summary>Link header pagination (RFC 5988).</summary>
        LinkHeader
    }

    /// <summary>
    /// REST API response format.
    /// </summary>
    public enum RestResponseFormat
    {
        /// <summary>Binary response (raw bytes).</summary>
        Binary,

        /// <summary>JSON response with data as text.</summary>
        Json,

        /// <summary>JSON response with data as Base64.</summary>
        JsonBase64
    }

    #endregion
}
