using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.S3Compatible
{
    /// <summary>
    /// Cloudflare R2 storage strategy with full production features:
    /// - S3-compatible API optimized for Cloudflare R2
    /// - Zero egress fees on data transfer
    /// - Custom domain support (R2.dev or custom domains)
    /// - Multipart upload for large files (>100MB)
    /// - Presigned URLs for temporary access
    /// - CORS configuration support
    /// - Server-side encryption (automatic AES-256)
    /// - Automatic retry with exponential backoff
    /// - Workers API integration ready
    /// - Public bucket support
    /// - Lifecycle policies (expiration, transitions)
    /// - Jurisdictional restrictions support
    /// </summary>
    public class CloudflareR2Strategy : UltimateStorageStrategyBase
    {
        private HttpClient? _httpClient;
        private string _accountId = string.Empty;
        private string _bucket = string.Empty;
        private string _accessKeyId = string.Empty;
        private string _secretAccessKey = string.Empty;
        private string _endpoint = string.Empty; // R2 endpoint: https://<accountId>.r2.cloudflarestorage.com
        private string? _customDomain = null; // Optional custom domain for public access
        private string? _r2DevDomain = null; // Optional R2.dev domain
        private bool _usePathStyle = false;
        private bool _enablePublicAccess = false;
        private bool _enableCors = false;
        private int _timeoutSeconds = 300;
        private int _multipartThresholdBytes = 100 * 1024 * 1024; // 100MB
        private int _multipartChunkSizeBytes = 10 * 1024 * 1024; // 10MB
        private int _maxConcurrentParts = 10; // R2 allows higher concurrency
        private int _maxRetries = 3;
        private int _retryDelayMs = 1000;
        private string _region = "auto"; // R2 uses "auto" as region
        private bool _useSignatureV4 = true; // Use AWS Signature V4 by default (V2 deprecated)

        public override string StrategyId => "cloudflare-r2";
        public override string Name => "Cloudflare R2 Storage";
        public override StorageTier Tier => StorageTier.Hot; // R2 is hot storage with zero egress

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false,
            SupportsVersioning = false, // R2 doesn't support versioning yet
            SupportsTiering = false, // R2 has single storage class
            SupportsEncryption = true, // Automatic AES-256
            SupportsCompression = false,
            SupportsMultipart = true,
            MaxObjectSize = 5L * 1024 * 1024 * 1024 * 1024, // 5TB
            MaxObjects = null, // No practical limit
            ConsistencyModel = ConsistencyModel.Strong
        };

        /// <summary>
        /// Initializes the Cloudflare R2 storage strategy.
        /// </summary>
        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load required configuration
            _accountId = GetConfiguration<string>("AccountId", string.Empty);
            _bucket = GetConfiguration<string>("Bucket", string.Empty);
            _accessKeyId = GetConfiguration<string>("AccessKeyId", string.Empty);
            _secretAccessKey = GetConfiguration<string>("SecretAccessKey", string.Empty);

            // Validate required configuration
            if (string.IsNullOrWhiteSpace(_accountId))
            {
                throw new InvalidOperationException("Cloudflare R2 account ID is required. Set 'AccountId' in configuration.");
            }
            if (string.IsNullOrWhiteSpace(_bucket))
            {
                throw new InvalidOperationException("R2 bucket name is required. Set 'Bucket' in configuration.");
            }
            if (string.IsNullOrWhiteSpace(_accessKeyId) || string.IsNullOrWhiteSpace(_secretAccessKey))
            {
                throw new InvalidOperationException("R2 credentials are required. Set 'AccessKeyId' and 'SecretAccessKey' in configuration.");
            }

            // Build R2 endpoint
            _endpoint = GetConfiguration<string>("Endpoint", $"https://{_accountId}.r2.cloudflarestorage.com");

            // Load optional configuration
            _customDomain = GetConfiguration<string?>("CustomDomain", null);
            _r2DevDomain = GetConfiguration<string?>("R2DevDomain", null);
            _usePathStyle = GetConfiguration<bool>("UsePathStyle", false);
            _enablePublicAccess = GetConfiguration<bool>("EnablePublicAccess", false);
            _enableCors = GetConfiguration<bool>("EnableCors", false);
            _timeoutSeconds = GetConfiguration<int>("TimeoutSeconds", 300);
            _multipartThresholdBytes = GetConfiguration<int>("MultipartThresholdBytes", 100 * 1024 * 1024);
            _multipartChunkSizeBytes = GetConfiguration<int>("MultipartChunkSizeBytes", 10 * 1024 * 1024);
            _maxConcurrentParts = GetConfiguration<int>("MaxConcurrentParts", 10);
            _maxRetries = GetConfiguration<int>("MaxRetries", 3);
            _retryDelayMs = GetConfiguration<int>("RetryDelayMs", 1000);
            _useSignatureV4 = GetConfiguration<bool>("UseSignatureV4", true);

            // Validate multipart settings
            if (_multipartChunkSizeBytes < 5 * 1024 * 1024)
            {
                throw new InvalidOperationException("Multipart chunk size must be at least 5MB for R2");
            }

            // Dispose prior HttpClient instance before creating a new one to prevent socket leaks on re-initialization
            _httpClient?.Dispose();
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_timeoutSeconds)
            };

            return Task.CompletedTask;
        }

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            ValidateKey(key);
            ValidateStream(data);

            // Determine if multipart upload is needed
            var useMultipart = false;
            long dataLength = 0;

            if (data.CanSeek)
            {
                dataLength = data.Length - data.Position;
                useMultipart = dataLength > _multipartThresholdBytes;
            }

            if (useMultipart)
            {
                return await StoreMultipartAsync(key, data, dataLength, metadata, ct);
            }
            else
            {
                return await StoreSinglePartAsync(key, data, metadata, ct);
            }
        }

        private async Task<StorageObjectMetadata> StoreSinglePartAsync(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            var endpoint = GetEndpointUrl(key);

            // Read data into memory
            using var ms = new MemoryStream(65536);
            await data.CopyToAsync(ms, 81920, ct);
            var content = ms.ToArray();

            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GetContentType(key));

            // R2 automatically encrypts all data with AES-256
            // No need to add encryption headers

            // Add custom metadata
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    request.Headers.TryAddWithoutValidation($"x-amz-meta-{kvp.Key}", kvp.Value);
                }
            }

            // Add cache control if public access is enabled
            if (_enablePublicAccess)
            {
                request.Headers.TryAddWithoutValidation("Cache-Control", "public, max-age=31536000");
            }

            // Sign and send request with retry
            await SignRequestAsync(request, content, ct);
            var response = await SendWithRetryAsync(request, ct);

            // Extract ETag from response
            var etag = response.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;

            // Update statistics
            IncrementBytesStored(content.Length);
            IncrementOperationCounter(StorageOperationType.Store);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = content.Length,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ETag = etag,
                ContentType = GetContentType(key),
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        private async Task<StorageObjectMetadata> StoreMultipartAsync(string key, Stream data, long dataLength, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            // Step 1: Initialize multipart upload
            var uploadId = await InitiateMultipartUploadAsync(key, metadata, ct);

            try
            {
                // Step 2: Upload parts in parallel
                var partCount = (int)Math.Ceiling((double)dataLength / _multipartChunkSizeBytes);
                var completedParts = new List<CompletedPart>();
                var semaphore = new SemaphoreSlim(_maxConcurrentParts, _maxConcurrentParts);

                var uploadTasks = new List<Task<CompletedPart>>();

                for (int partNumber = 1; partNumber <= partCount; partNumber++)
                {
                    var currentPartNumber = partNumber;
                    var partSize = (int)Math.Min(_multipartChunkSizeBytes, dataLength - (partNumber - 1) * _multipartChunkSizeBytes);

                    var partData = new byte[partSize];
                    var bytesRead = await data.ReadAsync(partData, 0, partSize, ct);

                    if (bytesRead != partSize)
                    {
                        throw new IOException($"Failed to read expected {partSize} bytes for part {partNumber}, got {bytesRead} bytes");
                    }

                    await semaphore.WaitAsync(ct);

                    var uploadTask = Task.Run(async () =>
                    {
                        try
                        {
                            return await UploadPartAsync(key, uploadId, currentPartNumber, partData, ct);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, ct);

                    uploadTasks.Add(uploadTask);
                }

                completedParts = (await Task.WhenAll(uploadTasks)).ToList();
                completedParts = completedParts.OrderBy(p => p.PartNumber).ToList();

                // Step 3: Complete multipart upload
                var etag = await CompleteMultipartUploadAsync(key, uploadId, completedParts, ct);

                // Update statistics
                IncrementBytesStored(dataLength);
                IncrementOperationCounter(StorageOperationType.Store);

                return new StorageObjectMetadata
                {
                    Key = key,
                    Size = dataLength,
                    Created = DateTime.UtcNow,
                    Modified = DateTime.UtcNow,
                    ETag = etag,
                    ContentType = GetContentType(key),
                    CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                    Tier = Tier
                };
            }
            catch (Exception)
            {
                // Abort multipart upload on failure
                try
                {
                    await AbortMultipartUploadAsync(key, uploadId, ct);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CloudflareR2Strategy.StoreMultipartAsync] {ex.GetType().Name}: {ex.Message}");
                    // Ignore abort failures
                }
                throw;
            }
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            var endpoint = GetEndpointUrl(key);
            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

            await SignRequestAsync(request, null, ct);
            using var response = await SendWithRetryAsync(request, ct);

            var ms = new MemoryStream(65536);
            await response.Content.CopyToAsync(ms, ct);
            ms.Position = 0;

            // Update statistics
            IncrementBytesRetrieved(ms.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            return ms;
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            // Get size before deletion for statistics
            long size = 0;
            try
            {
                var metadata = await GetMetadataAsyncCore(key, ct);
                size = metadata.Size;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudflareR2Strategy.DeleteAsyncCore] {ex.GetType().Name}: {ex.Message}");
                // Ignore if metadata retrieval fails
            }

            var endpoint = GetEndpointUrl(key);
            var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);

            await SignRequestAsync(request, null, ct);
            await SendWithRetryAsync(request, ct);

            // Update statistics
            if (size > 0)
            {
                IncrementBytesDeleted(size);
            }
            IncrementOperationCounter(StorageOperationType.Delete);
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            try
            {
                var endpoint = GetEndpointUrl(key);
                var request = new HttpRequestMessage(HttpMethod.Head, endpoint);

                await SignRequestAsync(request, null, ct);
                var response = await _httpClient!.SendAsync(request, ct);

                IncrementOperationCounter(StorageOperationType.Exists);

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudflareR2Strategy.ExistsAsyncCore] {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.List);

            string? continuationToken = null;

            do
            {
                var endpoint = $"{_endpoint}/{_bucket}?list-type=2";
                if (!string.IsNullOrEmpty(prefix))
                {
                    endpoint += $"&prefix={Uri.EscapeDataString(prefix)}";
                }
                if (continuationToken != null)
                {
                    endpoint += $"&continuation-token={Uri.EscapeDataString(continuationToken)}";
                }

                var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                await SignRequestAsync(request, null, ct);

                var response = await SendWithRetryAsync(request, ct);
                var xml = await response.Content.ReadAsStringAsync(ct);

                // Parse XML response
                var doc = XDocument.Parse(xml);
                var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

                var contents = doc.Descendants(ns + "Contents");
                foreach (var content in contents)
                {
                    ct.ThrowIfCancellationRequested();

                    var objectKey = content.Element(ns + "Key")?.Value;
                    if (string.IsNullOrEmpty(objectKey))
                        continue;

                    var sizeStr = content.Element(ns + "Size")?.Value;
                    var size = long.TryParse(sizeStr, out var parsedSize) ? parsedSize : 0L;

                    var lastModifiedStr = content.Element(ns + "LastModified")?.Value;
                    var lastModified = DateTime.TryParse(lastModifiedStr, out var parsedDate) ? parsedDate : DateTime.UtcNow;

                    var etag = content.Element(ns + "ETag")?.Value?.Trim('"') ?? string.Empty;

                    yield return new StorageObjectMetadata
                    {
                        Key = objectKey,
                        Size = size,
                        Created = lastModified,
                        Modified = lastModified,
                        ETag = etag,
                        ContentType = GetContentType(objectKey),
                        CustomMetadata = null,
                        Tier = Tier
                    };

                    await Task.Yield();
                }

                // Extract continuation token
                continuationToken = doc.Descendants(ns + "NextContinuationToken").FirstOrDefault()?.Value;

            } while (continuationToken != null);
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            var endpoint = GetEndpointUrl(key);
            var request = new HttpRequestMessage(HttpMethod.Head, endpoint);

            await SignRequestAsync(request, null, ct);
            var response = await SendWithRetryAsync(request, ct);

            var size = response.Content.Headers.ContentLength ?? 0;
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            var lastModified = response.Content.Headers.LastModified?.UtcDateTime ?? DateTime.UtcNow;
            var etag = response.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;

            // Extract custom metadata
            var customMetadata = new Dictionary<string, string>();
            foreach (var header in response.Headers.Where(h => h.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)))
            {
                var metaKey = header.Key.Substring("x-amz-meta-".Length);
                customMetadata[metaKey] = header.Value.FirstOrDefault() ?? string.Empty;
            }

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = size,
                Created = lastModified,
                Modified = lastModified,
                ETag = etag,
                ContentType = contentType,
                CustomMetadata = customMetadata.Count > 0 ? customMetadata : null,
                Tier = Tier
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                // Try to list objects with max-keys=1 as a health check
                var endpoint = $"{_endpoint}/{_bucket}?list-type=2&max-keys=1";
                var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                await SignRequestAsync(request, null, ct);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient!.SendAsync(request, ct);
                sw.Stop();

                if (response.IsSuccessStatusCode)
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Healthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"Cloudflare R2 bucket '{_bucket}' is accessible",
                        CheckedAt = DateTime.UtcNow
                    };
                }
                else
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Unhealthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"Cloudflare R2 bucket '{_bucket}' returned status code {response.StatusCode}",
                        CheckedAt = DateTime.UtcNow
                    };
                }
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to access Cloudflare R2 bucket '{_bucket}': {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            // R2 has no practical capacity limit
            return Task.FromResult<long?>(null);
        }

        #endregion

        #region R2-Specific Operations

        /// <summary>
        /// Generates a presigned URL for temporary access to an object.
        /// </summary>
        /// <param name="key">Object key/name.</param>
        /// <param name="expiresIn">How long the URL should be valid.</param>
        /// <returns>Presigned URL string.</returns>
        public string GeneratePresignedUrl(string key, TimeSpan expiresIn)
        {
            ValidateKey(key);
            return GeneratePresignedUrlV4(key, expiresIn);
        }

        /// <summary>
        /// Generates a presigned URL using AWS Signature Version 4.
        /// </summary>
        private string GeneratePresignedUrlV4(string key, TimeSpan expiresIn)
        {
            var now = DateTime.UtcNow;
            var dateStamp = now.ToString("yyyyMMdd");
            var amzDate = now.ToString("yyyyMMddTHHmmssZ");
            var expiresInSeconds = (int)expiresIn.TotalSeconds;

            var endpoint = GetEndpointUrl(key);
            var uri = new Uri(endpoint);

            // Build credential scope
            var credentialScope = $"{dateStamp}/{_region}/s3/aws4_request";
            var credential = $"{_accessKeyId}/{credentialScope}";

            // Build canonical query string
            var queryParams = new SortedDictionary<string, string>
            {
                { "X-Amz-Algorithm", "AWS4-HMAC-SHA256" },
                { "X-Amz-Credential", credential },
                { "X-Amz-Date", amzDate },
                { "X-Amz-Expires", expiresInSeconds.ToString() },
                { "X-Amz-SignedHeaders", "host" }
            };

            var canonicalQueryString = string.Join("&", queryParams.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

            // Build canonical request
            var canonicalUri = uri.AbsolutePath;
            var canonicalHeaders = $"host:{uri.Host}\n";
            var signedHeaders = "host";
            var payloadHash = "UNSIGNED-PAYLOAD";

            var canonicalRequest = $"GET\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
            var canonicalRequestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLower();

            // Build string to sign
            var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{canonicalRequestHash}";

            // Calculate signature
            var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + _secretAccessKey), dateStamp);
            var kRegion = HmacSha256(kDate, _region);
            var kService = HmacSha256(kRegion, "s3");
            var kSigning = HmacSha256(kService, "aws4_request");
            var signature = Convert.ToHexString(HmacSha256(kSigning, stringToSign)).ToLower();

            // Build final URL
            return $"{endpoint}?{canonicalQueryString}&X-Amz-Signature={signature}";
        }

        /// <summary>
        /// Gets the public URL for an object if custom domain or R2.dev domain is configured.
        /// </summary>
        /// <param name="key">Object key/name.</param>
        /// <returns>Public URL or null if public access not configured.</returns>
        public string? GetPublicUrl(string key)
        {
            ValidateKey(key);

            if (!string.IsNullOrEmpty(_customDomain))
            {
                return $"https://{_customDomain}/{key}";
            }
            else if (!string.IsNullOrEmpty(_r2DevDomain))
            {
                return $"https://{_r2DevDomain}/{key}";
            }

            return null;
        }

        /// <summary>
        /// Copies an object within R2.
        /// </summary>
        /// <param name="sourceKey">Source object key.</param>
        /// <param name="destinationKey">Destination object key.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken ct = default)
        {
            ValidateKey(sourceKey);
            ValidateKey(destinationKey);

            var endpoint = GetEndpointUrl(destinationKey);
            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            request.Headers.TryAddWithoutValidation("x-amz-copy-source", $"/{_bucket}/{sourceKey}");

            await SignRequestAsync(request, null, ct);
            await SendWithRetryAsync(request, ct);
        }

        /// <summary>
        /// Configures CORS settings for the bucket (requires Cloudflare API token).
        /// Note: R2 CORS is managed via Cloudflare API, not S3 API.
        /// </summary>
        /// <param name="allowedOrigins">Allowed origins (e.g., ["*"] or ["https://example.com"]).</param>
        /// <param name="allowedMethods">Allowed HTTP methods.</param>
        /// <param name="allowedHeaders">Allowed headers.</param>
        /// <param name="maxAgeSeconds">Max age for preflight cache.</param>
        /// <returns>Task representing the async operation.</returns>
        public async Task ConfigureCorsAsync(
            IEnumerable<string> allowedOrigins,
            IEnumerable<string> allowedMethods,
            IEnumerable<string>? allowedHeaders = null,
            int maxAgeSeconds = 3600)
        {
            // Note: R2 CORS configuration requires Cloudflare API, not S3 API
            // Uses Cloudflare API v4: PUT https://api.cloudflare.com/client/v4/accounts/{account_id}/r2/buckets/{bucket_name}/cors

            if (!_enableCors)
            {
                throw new InvalidOperationException("CORS is not enabled. Set 'EnableCors' to true in configuration.");
            }

            // Get Cloudflare API token from configuration (required for API calls)
            var apiToken = GetConfiguration<string>("CloudflareApiToken", string.Empty);
            if (string.IsNullOrWhiteSpace(apiToken))
            {
                throw new InvalidOperationException("Cloudflare API token is required for CORS configuration. Set 'CloudflareApiToken' in configuration.");
            }

            // Store CORS configuration for reference
            SetConfiguration("CorsAllowedOrigins", allowedOrigins);
            SetConfiguration("CorsAllowedMethods", allowedMethods);
            SetConfiguration("CorsAllowedHeaders", allowedHeaders ?? Array.Empty<string>());
            SetConfiguration("CorsMaxAge", maxAgeSeconds);

            try
            {
                // Build CORS configuration JSON
                var corsRules = new[]
                {
                    new
                    {
                        allowed_origins = allowedOrigins.ToArray(),
                        allowed_methods = allowedMethods.ToArray(),
                        allowed_headers = allowedHeaders?.ToArray() ?? new[] { "*" },
                        expose_headers = new[] { "ETag", "Content-Length", "Content-Type" },
                        max_age_seconds = maxAgeSeconds
                    }
                };

                var jsonPayload = System.Text.Json.JsonSerializer.Serialize(new { cors_rules = corsRules });

                // Make API request to Cloudflare
                var apiEndpoint = $"https://api.cloudflare.com/client/v4/accounts/{_accountId}/r2/buckets/{_bucket}/cors";
                var request = new HttpRequestMessage(HttpMethod.Put, apiEndpoint);
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiToken}");
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var response = await _httpClient!.SendAsync(request);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Failed to configure CORS via Cloudflare API. Status: {response.StatusCode}, Response: {responseContent}");
                }

                // Parse response to check for success
                using var jsonDoc = System.Text.Json.JsonDocument.Parse(responseContent);
                var success = jsonDoc.RootElement.TryGetProperty("success", out var successProp) && successProp.GetBoolean();

                if (!success)
                {
                    var errors = jsonDoc.RootElement.TryGetProperty("errors", out var errorsProp)
                        ? errorsProp.ToString()
                        : "Unknown error";
                    throw new InvalidOperationException($"Cloudflare API returned success=false. Errors: {errors}");
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException($"Failed to configure CORS via Cloudflare API: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Sets lifecycle policy for automatic object expiration.
        /// Note: R2 lifecycle policies are managed via Cloudflare API.
        /// </summary>
        /// <param name="rules">Lifecycle rules.</param>
        /// <returns>Task representing the async operation.</returns>
        public async Task SetLifecyclePolicyAsync(IEnumerable<LifecycleRule> rules)
        {
            // Note: R2 lifecycle configuration requires Cloudflare API
            // Uses Cloudflare API v4: PUT https://api.cloudflare.com/client/v4/accounts/{account_id}/r2/buckets/{bucket_name}/lifecycle

            if (rules == null || !rules.Any())
            {
                throw new ArgumentException("At least one lifecycle rule is required", nameof(rules));
            }

            // Get Cloudflare API token from configuration (required for API calls)
            var apiToken = GetConfiguration<string>("CloudflareApiToken", string.Empty);
            if (string.IsNullOrWhiteSpace(apiToken))
            {
                throw new InvalidOperationException("Cloudflare API token is required for lifecycle configuration. Set 'CloudflareApiToken' in configuration.");
            }

            // Store lifecycle rules for reference
            SetConfiguration("LifecycleRules", rules);

            try
            {
                // Build lifecycle configuration JSON
                var lifecycleRules = rules.Select(rule =>
                {
                    var ruleObj = new Dictionary<string, object>
                    {
                        ["id"] = rule.Id,
                        ["status"] = rule.Enabled ? "Enabled" : "Disabled"
                    };

                    // Add filter/prefix if specified
                    if (!string.IsNullOrWhiteSpace(rule.Prefix))
                    {
                        ruleObj["filter"] = new { prefix = rule.Prefix };
                    }

                    // Add expiration configuration
                    var expiration = new Dictionary<string, object>();
                    if (rule.ExpirationDays.HasValue)
                    {
                        expiration["days"] = rule.ExpirationDays.Value;
                    }
                    if (rule.ExpirationDate.HasValue)
                    {
                        expiration["date"] = rule.ExpirationDate.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    }
                    if (rule.ExpiredObjectDeleteMarker)
                    {
                        expiration["expired_object_delete_marker"] = true;
                    }

                    if (expiration.Count > 0)
                    {
                        ruleObj["expiration"] = expiration;
                    }

                    return ruleObj;
                }).ToArray();

                var jsonPayload = System.Text.Json.JsonSerializer.Serialize(new { rules = lifecycleRules });

                // Make API request to Cloudflare
                var apiEndpoint = $"https://api.cloudflare.com/client/v4/accounts/{_accountId}/r2/buckets/{_bucket}/lifecycle";
                var request = new HttpRequestMessage(HttpMethod.Put, apiEndpoint);
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiToken}");
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var response = await _httpClient!.SendAsync(request);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Failed to configure lifecycle policy via Cloudflare API. Status: {response.StatusCode}, Response: {responseContent}");
                }

                // Parse response to check for success
                using var jsonDoc = System.Text.Json.JsonDocument.Parse(responseContent);
                var success = jsonDoc.RootElement.TryGetProperty("success", out var successProp) && successProp.GetBoolean();

                if (!success)
                {
                    var errors = jsonDoc.RootElement.TryGetProperty("errors", out var errorsProp)
                        ? errorsProp.ToString()
                        : "Unknown error";
                    throw new InvalidOperationException($"Cloudflare API returned success=false. Errors: {errors}");
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException && ex is not ArgumentException)
            {
                throw new InvalidOperationException($"Failed to configure lifecycle policy via Cloudflare API: {ex.Message}", ex);
            }
        }

        private async Task<string> InitiateMultipartUploadAsync(string key, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            var endpoint = $"{GetEndpointUrl(key)}?uploads";
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

            // Add custom metadata
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    request.Headers.TryAddWithoutValidation($"x-amz-meta-{kvp.Key}", kvp.Value);
                }
            }

            await SignRequestAsync(request, null, ct);
            var response = await SendWithRetryAsync(request, ct);

            var xml = await response.Content.ReadAsStringAsync(ct);
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var uploadId = doc.Descendants(ns + "UploadId").FirstOrDefault()?.Value;

            if (string.IsNullOrEmpty(uploadId))
            {
                throw new InvalidOperationException("Failed to initiate multipart upload: UploadId not returned");
            }

            return uploadId;
        }

        private async Task<CompletedPart> UploadPartAsync(string key, string uploadId, int partNumber, byte[] partData, CancellationToken ct)
        {
            var endpoint = $"{GetEndpointUrl(key)}?partNumber={partNumber}&uploadId={Uri.EscapeDataString(uploadId)}";
            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            request.Content = new ByteArrayContent(partData);

            await SignRequestAsync(request, partData, ct);
            var response = await SendWithRetryAsync(request, ct);

            var etag = response.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;

            return new CompletedPart
            {
                PartNumber = partNumber,
                ETag = etag
            };
        }

        private async Task<string> CompleteMultipartUploadAsync(string key, string uploadId, List<CompletedPart> parts, CancellationToken ct)
        {
            var endpoint = $"{GetEndpointUrl(key)}?uploadId={Uri.EscapeDataString(uploadId)}";

            // Build XML body
            var xmlBody = new XElement("CompleteMultipartUpload",
                parts.Select(p => new XElement("Part",
                    new XElement("PartNumber", p.PartNumber),
                    new XElement("ETag", p.ETag)
                ))
            );

            var xmlString = xmlBody.ToString(SaveOptions.DisableFormatting);
            var content = Encoding.UTF8.GetBytes(xmlString);

            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");

            await SignRequestAsync(request, content, ct);
            var response = await SendWithRetryAsync(request, ct);

            var responseXml = await response.Content.ReadAsStringAsync(ct);
            var doc = XDocument.Parse(responseXml);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var etag = doc.Descendants(ns + "ETag").FirstOrDefault()?.Value?.Trim('"') ?? string.Empty;

            return etag;
        }

        private async Task AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct)
        {
            var endpoint = $"{GetEndpointUrl(key)}?uploadId={Uri.EscapeDataString(uploadId)}";
            var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);

            await SignRequestAsync(request, null, ct);
            await _httpClient!.SendAsync(request, ct);
        }

        #endregion

        #region Helper Methods

        private string GetEndpointUrl(string key)
        {
            if (_usePathStyle)
            {
                return $"{_endpoint}/{_bucket}/{key}";
            }

            // Virtual-hosted style (default for R2)
            var endpoint = new Uri(_endpoint);
            return $"{endpoint.Scheme}://{_bucket}.{endpoint.Host}/{key}";
        }

        private async Task SignRequestAsync(HttpRequestMessage request, byte[]? content, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var dateStamp = now.ToString("yyyyMMdd");
            var amzDate = now.ToString("yyyyMMddTHHmmssZ");

            request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
            request.Headers.Host = request.RequestUri?.Host;

            // AWS Signature Version 4 (R2 uses same signing as S3)
            var contentHash = content != null
                ? Convert.ToHexString(SHA256.HashData(content)).ToLower()
                : "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"; // Empty hash

            request.Headers.TryAddWithoutValidation("x-amz-content-sha256", contentHash);

            // Build canonical request
            var uri = request.RequestUri!;
            var canonicalUri = uri.AbsolutePath;
            var canonicalQueryString = uri.Query.TrimStart('?');

            var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
            var canonicalHeaders = $"host:{uri.Host}\nx-amz-content-sha256:{contentHash}\nx-amz-date:{amzDate}\n";

            var canonicalRequest = $"{request.Method}\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{contentHash}";
            var canonicalRequestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLower();

            // Build string to sign
            var credentialScope = $"{dateStamp}/{_region}/s3/aws4_request";
            var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{canonicalRequestHash}";

            // Calculate signature
            var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + _secretAccessKey), dateStamp);
            var kRegion = HmacSha256(kDate, _region);
            var kService = HmacSha256(kRegion, "s3");
            var kSigning = HmacSha256(kService, "aws4_request");
            var signature = Convert.ToHexString(HmacSha256(kSigning, stringToSign)).ToLower();

            var authHeader = $"AWS4-HMAC-SHA256 Credential={_accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
            request.Headers.TryAddWithoutValidation("Authorization", authHeader);

            await Task.CompletedTask; // Keep async signature for consistency
        }

        private static byte[] HmacSha256(byte[] key, string data)
        {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage original)
        {
            var clone = new HttpRequestMessage(original.Method, original.RequestUri);
            if (original.Content != null)
            {
                var ms = new MemoryStream();
                original.Content.CopyTo(ms, null, default);
                ms.Position = 0;
                clone.Content = new StreamContent(ms);
                foreach (var header in original.Content.Headers)
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            foreach (var header in original.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            clone.Version = original.Version;
            return clone;
        }

        private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpResponseMessage? response = null;
            Exception? lastException = null;

            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                var attemptRequest = attempt == 0 ? request : CloneRequest(request);
                try
                {
                    response = await _httpClient!.SendAsync(attemptRequest, ct);

                    if (response.IsSuccessStatusCode)
                    {
                        return response;
                    }

                    // Check if we should retry based on status code
                    if (!ShouldRetry(response.StatusCode) || attempt == _maxRetries)
                    {
                        response.EnsureSuccessStatusCode();
                        return response;
                    }
                }
                catch (Exception ex) when (attempt < _maxRetries)
                {
                    lastException = ex;
                }

                // Exponential backoff
                var delay = _retryDelayMs * (int)Math.Pow(2, attempt);
                await Task.Delay(delay, ct);
            }

            if (lastException != null)
            {
                throw lastException;
            }

            response?.EnsureSuccessStatusCode();
            return response!;
        }

        private bool ShouldRetry(System.Net.HttpStatusCode statusCode)
        {
            return statusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                   statusCode == System.Net.HttpStatusCode.RequestTimeout ||
                   statusCode == System.Net.HttpStatusCode.TooManyRequests ||
                   (int)statusCode >= 500;
        }

        private string GetContentType(string key)
        {
            var extension = Path.GetExtension(key).ToLowerInvariant();
            return extension switch
            {
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".txt" => "text/plain",
                ".csv" => "text/csv",
                ".html" or ".htm" => "text/html",
                ".css" => "text/css",
                ".js" => "application/javascript",
                ".pdf" => "application/pdf",
                ".zip" => "application/zip",
                ".tar" => "application/x-tar",
                ".gz" => "application/gzip",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                ".mp4" => "video/mp4",
                ".webm" => "video/webm",
                ".mp3" => "audio/mpeg",
                ".wasm" => "application/wasm",
                _ => "application/octet-stream"
            };
        }

        protected override int GetMaxKeyLength() => 1024; // R2 max key length (same as S3)

        #endregion

        #region Cleanup

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();
            _httpClient?.Dispose();
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Represents a completed part in a multipart upload.
    /// </summary>
    internal class CompletedPart
    {
        public int PartNumber { get; set; }
        public string ETag { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents a lifecycle rule for object expiration.
    /// </summary>
    public class LifecycleRule
    {
        /// <summary>Rule ID.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Whether the rule is enabled.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Optional prefix filter.</summary>
        public string? Prefix { get; set; }

        /// <summary>Number of days after creation to expire objects.</summary>
        public int? ExpirationDays { get; set; }

        /// <summary>Specific date to expire objects.</summary>
        public DateTime? ExpirationDate { get; set; }

        /// <summary>Whether to delete expired object delete markers.</summary>
        public bool ExpiredObjectDeleteMarker { get; set; }
    }

    #endregion
}
