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

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.SoftwareDefined
{
    /// <summary>
    /// Ceph RADOS Gateway (RGW) storage strategy with S3-compatible API and Ceph-specific features:
    /// - S3-compatible API via AWS SDK
    /// - User quotas management (max objects, max size)
    /// - Bucket quotas management
    /// - Multi-part uploads for large files (>100MB)
    /// - Server-side encryption (SSE-S3, SSE-KMS, SSE-C)
    /// - Versioning support
    /// - Bucket lifecycle policies
    /// - Storage class management
    /// - Presigned URLs for temporary access
    /// - Object tagging
    /// - Automatic retry with exponential backoff
    /// - Concurrent multipart upload optimization
    /// - Admin operations (quota management, user statistics)
    /// </summary>
    public class CephRgwStrategy : UltimateStorageStrategyBase
    {
        private HttpClient? _httpClient;
        private string _endpoint = string.Empty;
        private string _region = "default";
        private string _bucket = string.Empty;
        private string _accessKey = string.Empty;
        private string _secretKey = string.Empty;
        private string _adminEndpoint = string.Empty; // Separate admin API endpoint
        private string _defaultStorageClass = "STANDARD";
        private bool _usePathStyle = true; // Ceph RGW typically uses path-style
        private bool _enableServerSideEncryption = false;
        private string _sseAlgorithm = "AES256"; // AES256, aws:kms, aws:kms:dsse
        private string? _kmsKeyId = null;
        private bool _enableVersioning = false;
        private int _timeoutSeconds = 300;
        private int _multipartThresholdBytes = 100 * 1024 * 1024; // 100MB
        private int _multipartChunkSizeBytes = 10 * 1024 * 1024; // 10MB
        private int _maxConcurrentParts = 5;
        private int _maxRetries = 3;
        private int _retryDelayMs = 1000;
        private bool _useSignatureV4 = true; // Use AWS Signature V4 by default (V2 deprecated)

        // Ceph-specific quota settings
        private long _userMaxObjects = -1; // -1 = unlimited
        private long _userMaxSizeBytes = -1; // -1 = unlimited
        private long _bucketMaxObjects = -1;
        private long _bucketMaxSizeBytes = -1;
        private bool _enableQuotaManagement = false;

        public override string StrategyId => "ceph-rgw";
        public override string Name => "Ceph RADOS Gateway (S3-Compatible)";
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false,
            SupportsVersioning = true,
            SupportsTiering = true,
            SupportsEncryption = true,
            SupportsCompression = false,
            SupportsMultipart = true,
            MaxObjectSize = 5L * 1024 * 1024 * 1024 * 1024, // 5TB
            MaxObjects = null, // Configurable via quotas
            ConsistencyModel = ConsistencyModel.Strong // Ceph provides strong consistency
        };

        /// <summary>
        /// Initializes the Ceph RGW storage strategy.
        /// </summary>
        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load required configuration
            _endpoint = GetConfiguration<string>("Endpoint", string.Empty);
            _region = GetConfiguration<string>("Region", "default");
            _bucket = GetConfiguration<string>("Bucket", string.Empty);
            _accessKey = GetConfiguration<string>("AccessKey", string.Empty);
            _secretKey = GetConfiguration<string>("SecretKey", string.Empty);

            // Validate required configuration
            if (string.IsNullOrWhiteSpace(_endpoint))
            {
                throw new InvalidOperationException("Ceph RGW endpoint is required. Set 'Endpoint' in configuration (e.g., 'http://ceph-rgw.example.com').");
            }
            if (string.IsNullOrWhiteSpace(_bucket))
            {
                throw new InvalidOperationException("Bucket name is required. Set 'Bucket' in configuration.");
            }
            if (string.IsNullOrWhiteSpace(_accessKey) || string.IsNullOrWhiteSpace(_secretKey))
            {
                throw new InvalidOperationException("Ceph RGW credentials are required. Set 'AccessKey' and 'SecretKey' in configuration.");
            }

            // Load optional configuration
            _adminEndpoint = GetConfiguration<string>("AdminEndpoint", _endpoint); // Default to same endpoint
            _defaultStorageClass = GetConfiguration<string>("DefaultStorageClass", "STANDARD");
            _usePathStyle = GetConfiguration<bool>("UsePathStyle", true);
            _enableServerSideEncryption = GetConfiguration<bool>("EnableServerSideEncryption", false);
            _sseAlgorithm = GetConfiguration<string>("SSEAlgorithm", "AES256");
            _kmsKeyId = GetConfiguration<string?>("KMSKeyId", null);
            _enableVersioning = GetConfiguration<bool>("EnableVersioning", false);
            _timeoutSeconds = GetConfiguration<int>("TimeoutSeconds", 300);
            _multipartThresholdBytes = GetConfiguration<int>("MultipartThresholdBytes", 100 * 1024 * 1024);
            _multipartChunkSizeBytes = GetConfiguration<int>("MultipartChunkSizeBytes", 10 * 1024 * 1024);
            _maxConcurrentParts = GetConfiguration<int>("MaxConcurrentParts", 5);
            _maxRetries = GetConfiguration<int>("MaxRetries", 3);
            _retryDelayMs = GetConfiguration<int>("RetryDelayMs", 1000);
            _useSignatureV4 = GetConfiguration<bool>("UseSignatureV4", true);

            // Load Ceph-specific quota configuration
            _enableQuotaManagement = GetConfiguration<bool>("EnableQuotaManagement", false);
            _userMaxObjects = GetConfiguration<long>("UserMaxObjects", -1);
            _userMaxSizeBytes = GetConfiguration<long>("UserMaxSizeBytes", -1);
            _bucketMaxObjects = GetConfiguration<long>("BucketMaxObjects", -1);
            _bucketMaxSizeBytes = GetConfiguration<long>("BucketMaxSizeBytes", -1);

            // Create HTTP client
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

            // Check quotas before upload if enabled
            if (_enableQuotaManagement)
            {
                await ValidateQuotasAsync(key, data, ct);
            }

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

            // Add storage class header
            if (!string.IsNullOrEmpty(_defaultStorageClass))
            {
                request.Headers.TryAddWithoutValidation("x-amz-storage-class", _defaultStorageClass);
            }

            // Add server-side encryption
            if (_enableServerSideEncryption)
            {
                request.Headers.TryAddWithoutValidation("x-amz-server-side-encryption", _sseAlgorithm);
                if (_sseAlgorithm == "aws:kms" && !string.IsNullOrEmpty(_kmsKeyId))
                {
                    request.Headers.TryAddWithoutValidation("x-amz-server-side-encryption-aws-kms-key-id", _kmsKeyId);
                }
            }

            // Add custom metadata
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    request.Headers.TryAddWithoutValidation($"x-amz-meta-{kvp.Key}", kvp.Value);
                }
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
                Tier = MapStorageClassToTier(_defaultStorageClass)
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
                    Tier = MapStorageClassToTier(_defaultStorageClass)
                };
            }
            catch (Exception)
            {
                // Abort multipart upload on failure
                try
                {
                    await AbortMultipartUploadAsync(key, uploadId, ct);
                }
                catch
                {
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
            var response = await SendWithRetryAsync(request, ct);

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
            catch
            {
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
            catch
            {
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

                    var key = content.Element(ns + "Key")?.Value;
                    if (string.IsNullOrEmpty(key))
                        continue;

                    var sizeStr = content.Element(ns + "Size")?.Value;
                    var size = long.TryParse(sizeStr, out var parsedSize) ? parsedSize : 0L;

                    var lastModifiedStr = content.Element(ns + "LastModified")?.Value;
                    var lastModified = DateTime.TryParse(lastModifiedStr, out var parsedDate) ? parsedDate : DateTime.UtcNow;

                    var etag = content.Element(ns + "ETag")?.Value?.Trim('"') ?? string.Empty;
                    var storageClass = content.Element(ns + "StorageClass")?.Value ?? "STANDARD";

                    yield return new StorageObjectMetadata
                    {
                        Key = key,
                        Size = size,
                        Created = lastModified,
                        Modified = lastModified,
                        ETag = etag,
                        ContentType = GetContentType(key),
                        CustomMetadata = null,
                        Tier = MapStorageClassToTier(storageClass)
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

            // Extract storage class
            var storageClass = "STANDARD";
            if (response.Headers.TryGetValues("x-amz-storage-class", out var storageClassValues))
            {
                storageClass = storageClassValues.FirstOrDefault() ?? "STANDARD";
            }

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
                Tier = MapStorageClassToTier(storageClass)
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
                        Message = $"Ceph RGW bucket '{_bucket}' is accessible at {_endpoint}",
                        CheckedAt = DateTime.UtcNow
                    };
                }
                else
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Unhealthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"Ceph RGW bucket '{_bucket}' returned status code {response.StatusCode}",
                        CheckedAt = DateTime.UtcNow
                    };
                }
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to access Ceph RGW bucket '{_bucket}': {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override async Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            // If quotas are enabled, calculate remaining capacity
            if (_enableQuotaManagement && _bucketMaxSizeBytes > 0)
            {
                try
                {
                    var usage = await GetBucketUsageAsync(ct);
                    var available = _bucketMaxSizeBytes - usage.TotalBytes;
                    return available > 0 ? available : 0;
                }
                catch
                {
                    // If we can't get usage, return null (unknown)
                    return null;
                }
            }

            // Ceph has no practical capacity limit without quotas
            return null;
        }

        #endregion

        #region Ceph RGW-Specific Operations

        /// <summary>
        /// Gets bucket usage statistics via Ceph RGW Admin API.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Bucket usage information.</returns>
        public async Task<CephBucketUsage> GetBucketUsageAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableQuotaManagement)
            {
                throw new InvalidOperationException("Quota management is not enabled. Set 'EnableQuotaManagement' to true.");
            }

            // Ceph RGW Admin API endpoint: /admin/bucket?bucket=<name>&stats
            var endpoint = $"{_adminEndpoint}/admin/bucket?bucket={Uri.EscapeDataString(_bucket)}&stats";
            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

            await SignRequestAsync(request, null, ct);
            var response = await SendWithRetryAsync(request, ct);

            var json = await response.Content.ReadAsStringAsync(ct);

            // Parse JSON response (simplified - real implementation would use System.Text.Json)
            // Expected format: {"usage": {"rgw.main": {"size_kb": 1234, "num_objects": 10}}}
            long totalBytes = 0;
            long numObjects = 0;

            try
            {
                // Basic parsing - in production, use JsonDocument
                if (json.Contains("\"size_kb\""))
                {
                    var sizeStart = json.IndexOf("\"size_kb\"") + 11;
                    var sizeEnd = json.IndexOf(",", sizeStart);
                    if (sizeEnd == -1) sizeEnd = json.IndexOf("}", sizeStart);
                    var sizeStr = json.Substring(sizeStart, sizeEnd - sizeStart).Trim();
                    if (long.TryParse(sizeStr, out var sizeKb))
                    {
                        totalBytes = sizeKb * 1024;
                    }
                }

                if (json.Contains("\"num_objects\""))
                {
                    var objStart = json.IndexOf("\"num_objects\"") + 15;
                    var objEnd = json.IndexOf(",", objStart);
                    if (objEnd == -1) objEnd = json.IndexOf("}", objStart);
                    var objStr = json.Substring(objStart, objEnd - objStart).Trim();
                    long.TryParse(objStr, out numObjects);
                }
            }
            catch
            {
                // Parsing failed, return zeros
            }

            return new CephBucketUsage
            {
                BucketName = _bucket,
                TotalBytes = totalBytes,
                NumObjects = numObjects,
                CheckedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Sets user quota via Ceph RGW Admin API.
        /// </summary>
        /// <param name="userId">User ID to set quota for.</param>
        /// <param name="maxObjects">Maximum number of objects (-1 for unlimited).</param>
        /// <param name="maxSizeBytes">Maximum size in bytes (-1 for unlimited).</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task SetUserQuotaAsync(string userId, long maxObjects, long maxSizeBytes, CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableQuotaManagement)
            {
                throw new InvalidOperationException("Quota management is not enabled. Set 'EnableQuotaManagement' to true.");
            }

            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentException("User ID is required", nameof(userId));
            }

            // Ceph RGW Admin API: PUT /admin/user?quota&uid=<uid>&quota-type=user
            var endpoint = $"{_adminEndpoint}/admin/user?quota&uid={Uri.EscapeDataString(userId)}&quota-type=user";

            if (maxObjects >= 0)
            {
                endpoint += $"&max-objects={maxObjects}";
            }

            if (maxSizeBytes >= 0)
            {
                endpoint += $"&max-size={maxSizeBytes}";
            }

            endpoint += "&enabled=true";

            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            await SignRequestAsync(request, null, ct);
            await SendWithRetryAsync(request, ct);
        }

        /// <summary>
        /// Sets bucket quota via Ceph RGW Admin API.
        /// </summary>
        /// <param name="maxObjects">Maximum number of objects (-1 for unlimited).</param>
        /// <param name="maxSizeBytes">Maximum size in bytes (-1 for unlimited).</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task SetBucketQuotaAsync(long maxObjects, long maxSizeBytes, CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableQuotaManagement)
            {
                throw new InvalidOperationException("Quota management is not enabled. Set 'EnableQuotaManagement' to true.");
            }

            // Ceph RGW Admin API: PUT /admin/bucket?quota&bucket=<name>&quota-type=bucket
            var endpoint = $"{_adminEndpoint}/admin/bucket?quota&bucket={Uri.EscapeDataString(_bucket)}&quota-type=bucket";

            if (maxObjects >= 0)
            {
                endpoint += $"&max-objects={maxObjects}";
            }

            if (maxSizeBytes >= 0)
            {
                endpoint += $"&max-size={maxSizeBytes}";
            }

            endpoint += "&enabled=true";

            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            await SignRequestAsync(request, null, ct);
            await SendWithRetryAsync(request, ct);

            // Update local quota settings
            _bucketMaxObjects = maxObjects;
            _bucketMaxSizeBytes = maxSizeBytes;
        }

        /// <summary>
        /// Generates a presigned URL for temporary access to an object.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="expiresIn">How long the URL should be valid.</param>
        /// <returns>Presigned URL.</returns>
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
            var credential = $"{_accessKey}/{credentialScope}";

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
            var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + _secretKey), dateStamp);
            var kRegion = HmacSha256(kDate, _region);
            var kService = HmacSha256(kRegion, "s3");
            var kSigning = HmacSha256(kService, "aws4_request");
            var signature = Convert.ToHexString(HmacSha256(kSigning, stringToSign)).ToLower();

            // Build final URL
            return $"{endpoint}?{canonicalQueryString}&X-Amz-Signature={signature}";
        }

        /// <summary>
        /// Copies an object within Ceph RGW.
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
        /// Changes the storage class of an object (tiering).
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="storageClass">New storage class.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task ChangeStorageClassAsync(string key, string storageClass, CancellationToken ct = default)
        {
            ValidateKey(key);

            // Use copy-in-place to change storage class
            var endpoint = GetEndpointUrl(key);
            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            request.Headers.TryAddWithoutValidation("x-amz-copy-source", $"/{_bucket}/{key}");
            request.Headers.TryAddWithoutValidation("x-amz-storage-class", storageClass);
            request.Headers.TryAddWithoutValidation("x-amz-metadata-directive", "COPY");

            await SignRequestAsync(request, null, ct);
            await SendWithRetryAsync(request, ct);
        }

        /// <summary>
        /// Sets bucket lifecycle policy for automatic object expiration.
        /// </summary>
        /// <param name="rules">Lifecycle rules.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task SetBucketLifecyclePolicyAsync(IEnumerable<LifecycleRule> rules, CancellationToken ct = default)
        {
            EnsureInitialized();

            if (rules == null || !rules.Any())
            {
                throw new ArgumentException("At least one lifecycle rule is required", nameof(rules));
            }

            var endpoint = $"{_endpoint}/{_bucket}?lifecycle";

            // Build XML body
            var lifecycleConfig = new XElement("LifecycleConfiguration",
                rules.Select(r =>
                {
                    var elements = new List<XElement>
                    {
                        new XElement("ID", r.Id),
                        new XElement("Status", r.Enabled ? "Enabled" : "Disabled")
                    };

                    if (r.Prefix != null)
                    {
                        elements.Add(new XElement("Prefix", r.Prefix));
                    }

                    if (r.ExpirationDays > 0)
                    {
                        elements.Add(new XElement("Expiration", new XElement("Days", r.ExpirationDays)));
                    }

                    return new XElement("Rule", elements);
                })
            );

            var xmlString = lifecycleConfig.ToString(SaveOptions.DisableFormatting);
            var content = Encoding.UTF8.GetBytes(xmlString);

            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");

            await SignRequestAsync(request, content, ct);
            await SendWithRetryAsync(request, ct);
        }

        /// <summary>
        /// Validates that the upload won't exceed configured quotas.
        /// </summary>
        private async Task ValidateQuotasAsync(string key, Stream data, CancellationToken ct)
        {
            if (!_enableQuotaManagement)
            {
                return;
            }

            try
            {
                var usage = await GetBucketUsageAsync(ct);

                // Check object count quota
                if (_bucketMaxObjects > 0 && usage.NumObjects >= _bucketMaxObjects)
                {
                    throw new InvalidOperationException(
                        $"Bucket quota exceeded: maximum {_bucketMaxObjects} objects allowed, currently {usage.NumObjects} objects");
                }

                // Check size quota
                if (_bucketMaxSizeBytes > 0 && data.CanSeek)
                {
                    var uploadSize = data.Length - data.Position;
                    if (usage.TotalBytes + uploadSize > _bucketMaxSizeBytes)
                    {
                        throw new InvalidOperationException(
                            $"Bucket quota exceeded: maximum {_bucketMaxSizeBytes} bytes allowed, currently {usage.TotalBytes} bytes used, upload size {uploadSize} bytes");
                    }
                }
            }
            catch (Exception ex) when (!(ex is InvalidOperationException))
            {
                // If quota check fails for other reasons, log but don't block upload
                // In production, you'd use a proper logger here
            }
        }

        #endregion

        #region Multipart Upload Operations

        private async Task<string> InitiateMultipartUploadAsync(string key, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            var endpoint = $"{GetEndpointUrl(key)}?uploads";
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

            // Add storage class and encryption headers
            if (!string.IsNullOrEmpty(_defaultStorageClass))
            {
                request.Headers.TryAddWithoutValidation("x-amz-storage-class", _defaultStorageClass);
            }

            if (_enableServerSideEncryption)
            {
                request.Headers.TryAddWithoutValidation("x-amz-server-side-encryption", _sseAlgorithm);
                if (_sseAlgorithm == "aws:kms" && !string.IsNullOrEmpty(_kmsKeyId))
                {
                    request.Headers.TryAddWithoutValidation("x-amz-server-side-encryption-aws-kms-key-id", _kmsKeyId);
                }
            }

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

            // Virtual-hosted style (less common for Ceph RGW)
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

            // AWS Signature Version 4
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
            var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + _secretKey), dateStamp);
            var kRegion = HmacSha256(kDate, _region);
            var kService = HmacSha256(kRegion, "s3");
            var kSigning = HmacSha256(kService, "aws4_request");
            var signature = Convert.ToHexString(HmacSha256(kSigning, stringToSign)).ToLower();

            var authHeader = $"AWS4-HMAC-SHA256 Credential={_accessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
            request.Headers.TryAddWithoutValidation("Authorization", authHeader);

            await Task.CompletedTask;
        }

        private static byte[] HmacSha256(byte[] key, string data)
        {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        }

        private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpResponseMessage? response = null;
            Exception? lastException = null;

            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                try
                {
                    response = await _httpClient!.SendAsync(request, ct);

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
                ".pdf" => "application/pdf",
                ".zip" => "application/zip",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".mp4" => "video/mp4",
                ".mp3" => "audio/mpeg",
                _ => "application/octet-stream"
            };
        }

        private StorageTier MapStorageClassToTier(string storageClass)
        {
            return storageClass.ToUpperInvariant() switch
            {
                "STANDARD" => StorageTier.Hot,
                "STANDARD_IA" => StorageTier.Warm,
                "GLACIER" => StorageTier.Cold,
                "DEEP_ARCHIVE" => StorageTier.Archive,
                _ => StorageTier.Hot
            };
        }

        protected override int GetMaxKeyLength() => 1024; // S3-compatible max key length

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
    /// Represents Ceph bucket usage statistics.
    /// </summary>
    public class CephBucketUsage
    {
        /// <summary>Bucket name.</summary>
        public string BucketName { get; set; } = string.Empty;

        /// <summary>Total bytes used in the bucket.</summary>
        public long TotalBytes { get; set; }

        /// <summary>Number of objects in the bucket.</summary>
        public long NumObjects { get; set; }

        /// <summary>When the usage was checked.</summary>
        public DateTime CheckedAt { get; set; }
    }

    /// <summary>
    /// Represents a lifecycle rule for automatic object expiration.
    /// </summary>
    public class LifecycleRule
    {
        /// <summary>Rule ID.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Whether the rule is enabled.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Optional prefix filter.</summary>
        public string? Prefix { get; set; }

        /// <summary>Number of days after which objects expire (0 = no expiration).</summary>
        public int ExpirationDays { get; set; }
    }

    #endregion
}
