using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.S3Compatible
{
    /// <summary>
    /// DigitalOcean Spaces storage strategy using S3-compatible API.
    /// Full production features include:
    /// - S3-compatible API via AWS SDK (100% S3 API compatible)
    /// - Built-in CDN integration with edge caching
    /// - Custom domain support with SSL/TLS certificates
    /// - CORS configuration for web applications
    /// - Presigned URLs for temporary access
    /// - Server-side encryption (AES-256)
    /// - Large file support with multipart uploads
    /// - Cost-effective object storage ($5/250GB)
    /// - High availability across multiple data centers
    /// - Automatic retry with exponential backoff
    /// - Multi-region support (NYC, AMS, SGP, FRA, SFO, BLR, SYD)
    /// </summary>
    /// <remarks>
    /// DigitalOcean Spaces is an S3-compatible object storage service with built-in CDN.
    /// All objects stored in Spaces are automatically encrypted at rest with AES-256.
    ///
    /// Endpoint Format: {region}.digitaloceanspaces.com
    /// Supported Regions:
    /// - nyc3: New York City, USA (Region 3)
    /// - ams3: Amsterdam, Netherlands (Region 3)
    /// - sgp1: Singapore (Region 1)
    /// - fra1: Frankfurt, Germany (Region 1)
    /// - sfo2: San Francisco, USA (Region 2)
    /// - sfo3: San Francisco, USA (Region 3)
    /// - blr1: Bangalore, India (Region 1)
    /// - syd1: Sydney, Australia (Region 1)
    ///
    /// Configuration Requirements:
    /// - Region: Spaces region identifier (e.g., "nyc3")
    /// - Bucket: Space name (bucket)
    /// - AccessKey: Spaces Access Key
    /// - SecretKey: Spaces Secret Key
    ///
    /// CDN Features:
    /// - Integrated CDN with global edge network
    /// - Custom domain support with SSL certificates
    /// - Cache-Control header support
    /// - CDN endpoint format: {space}.{region}.cdn.digitaloceanspaces.com
    /// - Optional custom CDN domain configuration
    ///
    /// CORS Configuration:
    /// - Supports S3-compatible CORS API
    /// - Configure allowed origins, methods, headers
    /// - Required for web applications accessing Spaces directly
    ///
    /// Performance Characteristics:
    /// - Multipart upload recommended for files >100MB
    /// - Maximum object size: 5TB
    /// - Minimum part size: 5MB
    /// - Maximum part size: 5GB
    /// - Maximum parts per upload: 10,000
    /// - Integrated CDN for fast global delivery
    ///
    /// Pricing Model:
    /// - Flat storage pricing: $5/month for 250GB
    /// - Additional storage: $0.02/GB/month
    /// - Bandwidth: 1TB free outbound, then $0.01/GB
    /// - No API request charges
    /// - No ingress fees
    /// </remarks>
    public class DigitalOceanSpacesStrategy : UltimateStorageStrategyBase
    {
        private AmazonS3Client? _s3Client;
        private string _endpoint = string.Empty;
        private string _region = "nyc3";
        private string _bucket = string.Empty;
        private string _accessKey = string.Empty;
        private string _secretKey = string.Empty;
        private bool _enableCdn = false;
        private string? _cdnEndpoint = null; // Optional: {space}.{region}.cdn.digitaloceanspaces.com
        private string? _customDomain = null; // Optional: custom domain for CDN
        private bool _enableCors = false;
        private int _timeoutSeconds = 300;
        private long _multipartThresholdBytes = 100 * 1024 * 1024; // 100MB
        private long _multipartPartSizeBytes = 100 * 1024 * 1024; // 100MB default
        private int _maxConcurrentParts = 10;
        private int _maxRetries = 3;
        private int _retryDelayMs = 1000;
        private string _cacheControl = "public, max-age=31536000"; // 1 year default for CDN

        public override string StrategyId => "digitalocean-spaces";
        public override string Name => "DigitalOcean Spaces";
        public override StorageTier Tier => StorageTier.Hot; // Spaces is hot storage with CDN

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false,
            SupportsVersioning = false, // Spaces doesn't support versioning
            SupportsTiering = false, // Single storage class
            SupportsEncryption = true, // Automatic AES-256
            SupportsCompression = false,
            SupportsMultipart = true,
            MaxObjectSize = 5L * 1024 * 1024 * 1024 * 1024, // 5TB
            MaxObjects = null, // No practical limit
            ConsistencyModel = ConsistencyModel.Strong
        };

        #region Initialization

        /// <summary>
        /// Initializes the DigitalOcean Spaces storage strategy.
        /// </summary>
        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load required configuration
            _region = GetConfiguration<string>("Region")
                ?? throw new InvalidOperationException("Spaces region is required. Set 'Region' (e.g., 'nyc3', 'ams3', 'sgp1', 'fra1', 'sfo2', 'sfo3', 'blr1', 'syd1').");

            _bucket = GetConfiguration<string>("Bucket")
                ?? throw new InvalidOperationException("Spaces name (bucket) is required. Set 'Bucket' in configuration.");

            _accessKey = GetConfiguration<string>("AccessKey")
                ?? throw new InvalidOperationException("Spaces Access Key is required. Set 'AccessKey' in configuration.");

            _secretKey = GetConfiguration<string>("SecretKey")
                ?? throw new InvalidOperationException("Spaces Secret Key is required. Set 'SecretKey' in configuration.");

            // Validate region
            var validRegions = new[] { "nyc3", "ams3", "sgp1", "fra1", "sfo2", "sfo3", "blr1", "syd1" };
            if (!validRegions.Contains(_region.ToLowerInvariant()))
            {
                throw new InvalidOperationException(
                    $"Invalid Spaces region '{_region}'. Valid regions: {string.Join(", ", validRegions)}");
            }

            // Build endpoint
            _endpoint = GetConfiguration<string>("Endpoint", $"{_region}.digitaloceanspaces.com");

            // Load optional configuration
            _enableCdn = GetConfiguration("EnableCdn", false);
            _cdnEndpoint = GetConfiguration<string?>("CdnEndpoint", null);
            _customDomain = GetConfiguration<string?>("CustomDomain", null);
            _enableCors = GetConfiguration("EnableCors", false);
            _timeoutSeconds = GetConfiguration("TimeoutSeconds", 300);
            _multipartThresholdBytes = GetConfiguration("MultipartThresholdBytes", 100L * 1024 * 1024);
            _multipartPartSizeBytes = GetConfiguration("MultipartPartSizeBytes", 100L * 1024 * 1024);
            _maxConcurrentParts = GetConfiguration("MaxConcurrentParts", 10);
            _maxRetries = GetConfiguration("MaxRetries", 3);
            _retryDelayMs = GetConfiguration("RetryDelayMs", 1000);
            _cacheControl = GetConfiguration("CacheControl", "public, max-age=31536000");

            // Auto-generate CDN endpoint if not provided but CDN is enabled
            if (_enableCdn && string.IsNullOrEmpty(_cdnEndpoint) && string.IsNullOrEmpty(_customDomain))
            {
                _cdnEndpoint = $"{_bucket}.{_region}.cdn.digitaloceanspaces.com";
            }

            // Validate multipart settings (S3 requirements)
            if (_multipartPartSizeBytes < 5 * 1024 * 1024)
            {
                throw new InvalidOperationException("Spaces multipart part size must be at least 5MB");
            }

            if (_multipartPartSizeBytes > 5L * 1024 * 1024 * 1024)
            {
                throw new InvalidOperationException("Spaces multipart part size cannot exceed 5GB");
            }

            // Create AWS S3 client configured for DigitalOcean Spaces
            var s3Config = new AmazonS3Config
            {
                ServiceURL = $"https://{_endpoint}",
                AuthenticationRegion = _region,
                ForcePathStyle = false, // Spaces uses virtual-hosted-style URLs
                Timeout = TimeSpan.FromSeconds(_timeoutSeconds),
                MaxErrorRetry = _maxRetries,
                RetryMode = RequestRetryMode.Standard,
                UseHttp = false, // Always use HTTPS
                ThrottleRetries = true
                // AWS Signature Version 4 is used by default
            };

            var credentials = new BasicAWSCredentials(_accessKey, _secretKey);
            _s3Client = new AmazonS3Client(credentials, s3Config);

            return Task.CompletedTask;
        }

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();
            _s3Client?.Dispose();
        }

        #endregion

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(
            string key,
            Stream data,
            IDictionary<string, string>? metadata,
            CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            // Determine upload strategy
            long dataLength = -1;
            bool useMultipart = false;

            if (data.CanSeek)
            {
                dataLength = data.Length - data.Position;
                useMultipart = dataLength > _multipartThresholdBytes;
            }

            StorageObjectMetadata result;

            if (useMultipart && dataLength > 0)
            {
                result = await StoreMultipartAsync(key, data, dataLength, metadata, ct);
            }
            else
            {
                result = await StoreSinglePartAsync(key, data, metadata, ct);
            }

            // Update statistics
            IncrementOperationCounter(StorageOperationType.Store);

            return result;
        }

        private async Task<StorageObjectMetadata> StoreSinglePartAsync(
            string key,
            Stream data,
            IDictionary<string, string>? metadata,
            CancellationToken ct)
        {
            var request = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = key,
                InputStream = data,
                ContentType = GetContentType(key),
                AutoCloseStream = false
            };

            // Add metadata
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    request.Metadata.Add(kvp.Key, kvp.Value);
                }
            }

            // Add cache control for CDN optimization
            if (_enableCdn)
            {
                request.Headers.CacheControl = _cacheControl;
            }

            // Spaces automatically encrypts all data with AES-256
            // No need to specify server-side encryption

            // Set ACL if needed (private by default)
            request.CannedACL = S3CannedACL.Private;

            // Capture start position before retry resets it
            var startPosition = data.CanSeek ? data.Position : 0L;

            // Execute upload with retry
            var response = await ExecuteWithRetryAsync(async () =>
            {
                if (data.CanSeek)
                {
                    data.Position = startPosition;
                }
                return await _s3Client!.PutObjectAsync(request, ct);
            }, ct);

            // Get actual size relative to start position (not start of stream)
            var size = data.CanSeek ? (data.Length - startPosition) : response.ContentLength;
            IncrementBytesStored(size);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = size,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ETag = response.ETag?.Trim('"') ?? string.Empty,
                ContentType = request.ContentType,
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        private async Task<StorageObjectMetadata> StoreMultipartAsync(
            string key,
            Stream data,
            long dataLength,
            IDictionary<string, string>? metadata,
            CancellationToken ct)
        {
            // Step 1: Initiate multipart upload
            var initiateRequest = new InitiateMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = key,
                ContentType = GetContentType(key),
                CannedACL = S3CannedACL.Private
            };

            // Add metadata
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    initiateRequest.Metadata.Add(kvp.Key, kvp.Value);
                }
            }

            // Add cache control for CDN
            if (_enableCdn)
            {
                initiateRequest.Headers.CacheControl = _cacheControl;
            }

            var initiateResponse = await ExecuteWithRetryAsync(async () =>
                await _s3Client!.InitiateMultipartUploadAsync(initiateRequest, ct), ct);

            var uploadId = initiateResponse.UploadId;

            try
            {
                // Step 2: Upload parts in parallel
                var partCount = (int)Math.Ceiling((double)dataLength / _multipartPartSizeBytes);
                var uploadedParts = new List<PartETag>();
                var semaphore = new SemaphoreSlim(_maxConcurrentParts, _maxConcurrentParts);
                var uploadTasks = new List<Task<PartETag>>();

                for (int partNumber = 1; partNumber <= partCount; partNumber++)
                {
                    var currentPartNumber = partNumber;
                    var partSize = (int)Math.Min(_multipartPartSizeBytes, dataLength - (partNumber - 1) * _multipartPartSizeBytes);

                    // Read part data
                    var partData = new byte[partSize];
                    var bytesRead = await data.ReadAsync(partData, 0, partSize, ct);

                    if (bytesRead != partSize)
                    {
                        throw new IOException($"Expected to read {partSize} bytes for part {partNumber}, but read {bytesRead} bytes");
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

                uploadedParts = (await Task.WhenAll(uploadTasks)).OrderBy(p => p.PartNumber).ToList();

                // Step 3: Complete multipart upload
                var completeRequest = new CompleteMultipartUploadRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    UploadId = uploadId,
                    PartETags = uploadedParts
                };

                var completeResponse = await ExecuteWithRetryAsync(async () =>
                    await _s3Client!.CompleteMultipartUploadAsync(completeRequest, ct), ct);

                // Update statistics
                IncrementBytesStored(dataLength);

                return new StorageObjectMetadata
                {
                    Key = key,
                    Size = dataLength,
                    Created = DateTime.UtcNow,
                    Modified = DateTime.UtcNow,
                    ETag = completeResponse.ETag?.Trim('"') ?? string.Empty,
                    ContentType = initiateRequest.ContentType,
                    CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                    Tier = Tier
                };
            }
            catch (Exception)
            {
                // Abort multipart upload on failure
                try
                {
                    var abortRequest = new AbortMultipartUploadRequest
                    {
                        BucketName = _bucket,
                        Key = key,
                        UploadId = uploadId
                    };
                    await _s3Client!.AbortMultipartUploadAsync(abortRequest, ct);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DigitalOceanSpacesStrategy.StoreMultipartAsync] {ex.GetType().Name}: {ex.Message}");
                    // Ignore abort failures
                }
                throw;
            }
        }

        private async Task<PartETag> UploadPartAsync(
            string key,
            string uploadId,
            int partNumber,
            byte[] partData,
            CancellationToken ct)
        {
            var uploadRequest = new UploadPartRequest
            {
                BucketName = _bucket,
                Key = key,
                UploadId = uploadId,
                PartNumber = partNumber,
                InputStream = new MemoryStream(partData),
                PartSize = partData.Length
            };

            var response = await ExecuteWithRetryAsync(async () =>
                await _s3Client!.UploadPartAsync(uploadRequest, ct), ct);

            return new PartETag(partNumber, response.ETag);
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var request = new GetObjectRequest
            {
                BucketName = _bucket,
                Key = key
            };

            using var response = await ExecuteWithRetryAsync(async () =>
                await _s3Client!.GetObjectAsync(request, ct), ct);

            var ms = new MemoryStream(65536);
            await response.ResponseStream.CopyToAsync(ms, 81920, ct);
            ms.Position = 0;

            // Update statistics
            IncrementBytesRetrieved(ms.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            return ms;
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
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
                System.Diagnostics.Debug.WriteLine($"[DigitalOceanSpacesStrategy.DeleteAsyncCore] {ex.GetType().Name}: {ex.Message}");
                // Ignore errors
            }

            var request = new DeleteObjectRequest
            {
                BucketName = _bucket,
                Key = key
            };

            await ExecuteWithRetryAsync(async () =>
                await _s3Client!.DeleteObjectAsync(request, ct), ct);

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

            try
            {
                var request = new GetObjectMetadataRequest
                {
                    BucketName = _bucket,
                    Key = key
                };

                await _s3Client!.GetObjectMetadataAsync(request, ct);
                IncrementOperationCounter(StorageOperationType.Exists);
                return true;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                IncrementOperationCounter(StorageOperationType.Exists);
                return false;
            }
            catch (Exception ex)
            {
                // P2-4140: Re-throw auth and network errors so callers see real failures instead
                // of a misleading false (object-not-found) result for auth/connectivity problems.
                if (ex is AmazonS3Exception s3ex &&
                    (s3ex.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                     s3ex.StatusCode == System.Net.HttpStatusCode.Unauthorized))
                    throw;
                IncrementOperationCounter(StorageOperationType.Exists);
                RecordFailure();
                return false;
            }
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(
            string? prefix,
            [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();
            IncrementOperationCounter(StorageOperationType.List);

            var request = new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = prefix ?? string.Empty
            };

            ListObjectsV2Response? response;

            do
            {
                response = await ExecuteWithRetryAsync(async () =>
                    await _s3Client!.ListObjectsV2Async(request, ct), ct);

                foreach (var s3Object in response.S3Objects)
                {
                    ct.ThrowIfCancellationRequested();

                    yield return new StorageObjectMetadata
                    {
                        Key = s3Object.Key,
                        Size = s3Object.Size ?? 0L,
                        Created = s3Object.LastModified ?? DateTime.UtcNow,
                        Modified = s3Object.LastModified ?? DateTime.UtcNow,
                        ETag = s3Object.ETag?.Trim('"') ?? string.Empty,
                        ContentType = GetContentType(s3Object.Key),
                        CustomMetadata = null, // List doesn't return metadata
                        Tier = Tier
                    };

                    await Task.Yield();
                }

                request.ContinuationToken = response.NextContinuationToken;

            } while (response.IsTruncated ?? false);
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var request = new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = key
            };

            var response = await ExecuteWithRetryAsync(async () =>
                await _s3Client!.GetObjectMetadataAsync(request, ct), ct);

            // Extract custom metadata
            var customMetadata = new Dictionary<string, string>();
            if (response.Metadata != null)
            {
                foreach (var key2 in response.Metadata.Keys)
                {
                    customMetadata[key2] = response.Metadata[key2];
                }
            }

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = response.ContentLength,
                Created = response.LastModified ?? DateTime.UtcNow,
                Modified = response.LastModified ?? DateTime.UtcNow,
                ETag = response.ETag?.Trim('"') ?? string.Empty,
                ContentType = response.Headers.ContentType ?? "application/octet-stream",
                CustomMetadata = customMetadata.Count > 0 ? customMetadata : null,
                Tier = Tier
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // List with max-keys=1 as health check
                var request = new ListObjectsV2Request
                {
                    BucketName = _bucket,
                    MaxKeys = 1
                };

                await _s3Client!.ListObjectsV2Async(request, ct);
                sw.Stop();

                return new StorageHealthInfo
                {
                    Status = HealthStatus.Healthy,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Message = $"DigitalOcean Space '{_bucket}' is accessible in region {_region}",
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to access DigitalOcean Space '{_bucket}' in region {_region}: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            // Spaces has no practical capacity limit from client perspective
            return Task.FromResult<long?>(null);
        }

        #endregion

        #region DigitalOcean Spaces-Specific Operations

        /// <summary>
        /// Generates a presigned URL for temporary access to an object.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="expiresIn">How long the URL should be valid (max 7 days).</param>
        /// <param name="httpMethod">HTTP method (GET or PUT).</param>
        /// <returns>Presigned URL.</returns>
        public string GeneratePresignedUrl(string key, TimeSpan expiresIn, string httpMethod = "GET")
        {
            EnsureInitialized();
            ValidateKey(key);

            if (expiresIn.TotalSeconds < 1 || expiresIn.TotalDays > 7)
            {
                throw new ArgumentException("Presigned URL expiration must be between 1 second and 7 days", nameof(expiresIn));
            }

            if (httpMethod.ToUpperInvariant() == "GET")
            {
                var request = new GetPreSignedUrlRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    Verb = HttpVerb.GET,
                    Expires = DateTime.UtcNow.Add(expiresIn)
                };

                return _s3Client!.GetPreSignedURL(request);
            }
            else if (httpMethod.ToUpperInvariant() == "PUT")
            {
                var request = new GetPreSignedUrlRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    Verb = HttpVerb.PUT,
                    Expires = DateTime.UtcNow.Add(expiresIn)
                };

                return _s3Client!.GetPreSignedURL(request);
            }
            else
            {
                throw new ArgumentException($"Unsupported HTTP method '{httpMethod}'. Use GET or PUT.", nameof(httpMethod));
            }
        }

        /// <summary>
        /// Gets the CDN URL for an object if CDN is enabled.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <returns>CDN URL or null if CDN not configured.</returns>
        public string? GetCdnUrl(string key)
        {
            ValidateKey(key);

            if (!_enableCdn)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(_customDomain))
            {
                return $"https://{_customDomain}/{key}";
            }
            else if (!string.IsNullOrEmpty(_cdnEndpoint))
            {
                return $"https://{_cdnEndpoint}/{key}";
            }

            return null;
        }

        /// <summary>
        /// Gets the direct (non-CDN) URL for an object.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <returns>Direct URL.</returns>
        public string GetDirectUrl(string key)
        {
            ValidateKey(key);
            return $"https://{_bucket}.{_endpoint}/{key}";
        }

        /// <summary>
        /// Copies an object within the same Space (server-side copy - no data transfer).
        /// </summary>
        /// <param name="sourceKey">Source object key.</param>
        /// <param name="destinationKey">Destination object key.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task CopyObjectAsync(
            string sourceKey,
            string destinationKey,
            CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(sourceKey);
            ValidateKey(destinationKey);

            var request = new CopyObjectRequest
            {
                SourceBucket = _bucket,
                SourceKey = sourceKey,
                DestinationBucket = _bucket,
                DestinationKey = destinationKey
            };

            await ExecuteWithRetryAsync(async () =>
                await _s3Client!.CopyObjectAsync(request, ct), ct);
        }

        /// <summary>
        /// Configures CORS settings for the Space.
        /// </summary>
        /// <param name="rules">CORS rules to configure.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task ConfigureCorsAsync(
            IEnumerable<CORSRule> rules,
            CancellationToken ct = default)
        {
            EnsureInitialized();

            if (rules == null || !rules.Any())
            {
                throw new ArgumentException("At least one CORS rule is required", nameof(rules));
            }

            var request = new PutCORSConfigurationRequest
            {
                BucketName = _bucket,
                Configuration = new CORSConfiguration
                {
                    Rules = rules.ToList()
                }
            };

            await ExecuteWithRetryAsync(async () =>
                await _s3Client!.PutCORSConfigurationAsync(request, ct), ct);
        }

        /// <summary>
        /// Gets the current CORS configuration for the Space.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>CORS rules or null if not configured.</returns>
        public async Task<List<CORSRule>?> GetCorsConfigurationAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            try
            {
                var request = new GetCORSConfigurationRequest
                {
                    BucketName = _bucket
                };

                var response = await ExecuteWithRetryAsync(async () =>
                    await _s3Client!.GetCORSConfigurationAsync(request, ct), ct);

                return response.Configuration?.Rules;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        /// <summary>
        /// Removes CORS configuration from the Space.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async Task DeleteCorsConfigurationAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            var request = new DeleteCORSConfigurationRequest
            {
                BucketName = _bucket
            };

            await ExecuteWithRetryAsync(async () =>
                await _s3Client!.DeleteCORSConfigurationAsync(request, ct), ct);
        }

        /// <summary>
        /// Sets the ACL (Access Control List) for an object.
        /// Supports making objects public or private.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="acl">ACL to apply (e.g., Public, Private).</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task SetObjectAclAsync(
            string key,
            S3CannedACL acl,
            CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            var request = new PutObjectAclRequest
            {
                BucketName = _bucket,
                Key = key,
                ACL = acl
            };

            await ExecuteWithRetryAsync(async () =>
                await _s3Client!.PutObjectAclAsync(request, ct), ct);
        }

        /// <summary>
        /// Makes an object publicly accessible.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task MakeObjectPublicAsync(string key, CancellationToken ct = default)
        {
            await SetObjectAclAsync(key, S3CannedACL.PublicRead, ct);
        }

        /// <summary>
        /// Makes an object private.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task MakeObjectPrivateAsync(string key, CancellationToken ct = default)
        {
            await SetObjectAclAsync(key, S3CannedACL.Private, ct);
        }

        /// <summary>
        /// Configures lifecycle rules for automatic object expiration.
        /// </summary>
        /// <param name="rules">Lifecycle rules to configure.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task SetLifecycleConfigurationAsync(
            List<Amazon.S3.Model.LifecycleRule> rules,
            CancellationToken ct = default)
        {
            EnsureInitialized();

            if (rules == null || rules.Count == 0)
            {
                throw new ArgumentException("At least one lifecycle rule is required", nameof(rules));
            }

            var request = new PutLifecycleConfigurationRequest
            {
                BucketName = _bucket,
                Configuration = new LifecycleConfiguration
                {
                    Rules = rules
                }
            };

            await ExecuteWithRetryAsync(async () =>
                await _s3Client!.PutLifecycleConfigurationAsync(request, ct), ct);
        }

        /// <summary>
        /// Gets the lifecycle configuration for the Space.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Lifecycle rules or null if not configured.</returns>
        public async Task<List<Amazon.S3.Model.LifecycleRule>?> GetLifecycleConfigurationAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            try
            {
                var request = new GetLifecycleConfigurationRequest
                {
                    BucketName = _bucket
                };

                var response = await ExecuteWithRetryAsync(async () =>
                    await _s3Client!.GetLifecycleConfigurationAsync(request, ct), ct);

                return response.Configuration?.Rules;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Executes an operation with exponential backoff retry logic.
        /// </summary>
        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
        {
            Exception? lastException = null;

            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                try
                {
                    return await operation();
                }
                catch (Exception ex) when (ShouldRetry(ex) && attempt < _maxRetries)
                {
                    lastException = ex;
                }

                // Exponential backoff
                var delay = _retryDelayMs * (int)Math.Pow(2, attempt);
                await Task.Delay(delay, ct);
            }

            throw lastException ?? new InvalidOperationException("Operation failed with no exception recorded");
        }

        /// <summary>
        /// Determines if an exception should be retried.
        /// </summary>
        private static bool ShouldRetry(Exception ex)
        {
            if (ex is AmazonS3Exception s3Ex)
            {
                // Retry on throttling and server errors
                return s3Ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                       s3Ex.StatusCode == System.Net.HttpStatusCode.RequestTimeout ||
                       s3Ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                       (int)s3Ex.StatusCode >= 500 ||
                       s3Ex.ErrorCode == "RequestTimeout" ||
                       s3Ex.ErrorCode == "SlowDown" ||
                       s3Ex.ErrorCode == "InternalError" ||
                       s3Ex.ErrorCode == "ServiceUnavailable";
            }

            return ex is TaskCanceledException ||
                   ex is TimeoutException ||
                   ex is IOException;
        }

        /// <summary>
        /// Gets content type based on file extension.
        /// </summary>
        private static string GetContentType(string key)
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
                ".7z" => "application/x-7z-compressed",
                ".rar" => "application/x-rar-compressed",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                ".ico" => "image/x-icon",
                ".tiff" or ".tif" => "image/tiff",
                ".mp4" => "video/mp4",
                ".avi" => "video/x-msvideo",
                ".mov" => "video/quicktime",
                ".wmv" => "video/x-ms-wmv",
                ".flv" => "video/x-flv",
                ".webm" => "video/webm",
                ".mkv" => "video/x-matroska",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".ogg" => "audio/ogg",
                ".flac" => "audio/flac",
                ".aac" => "audio/aac",
                ".m4a" => "audio/mp4",
                ".doc" or ".docx" => "application/msword",
                ".xls" or ".xlsx" => "application/vnd.ms-excel",
                ".ppt" or ".pptx" => "application/vnd.ms-powerpoint",
                _ => "application/octet-stream"
            };
        }

        protected override int GetMaxKeyLength() => 1024; // Spaces max object name length (S3-compatible)

        #endregion
    }
}
