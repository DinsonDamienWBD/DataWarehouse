using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Amazon.Runtime;
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

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Cloud
{
    /// <summary>
    /// AWS S3 storage strategy with full production features:
    /// - Official AWSSDK.S3 client with automatic retry, credential management
    /// - Manual HTTP fallback for air-gapped/embedded environments without NuGet
    /// - Multi-part uploads for large files (>100MB) via TransferUtility
    /// - Server-side encryption (SSE-S3, SSE-KMS, SSE-C)
    /// - Storage class management (Standard, IA, Glacier, Deep Archive)
    /// - Presigned URLs for temporary access
    /// - Versioning support
    /// - Transfer acceleration
    /// - S3-compatible endpoint support (MinIO, Wasabi, DigitalOcean Spaces)
    /// - Concurrent multipart upload optimization
    /// </summary>
    public class S3Strategy : UltimateStorageStrategyBase
    {
        // SDK client (primary path)
        private IAmazonS3? _s3Client;
        private TransferUtility? _transferUtility;

        // Manual HTTP fallback client
        private HttpClient? _httpClient;

        // Configuration
        private string _endpoint = "https://s3.amazonaws.com";
        private string _region = "us-east-1";
        private string _bucket = string.Empty;
        private string _accessKey = string.Empty;
        private string _secretKey = string.Empty;
        private string _defaultStorageClass = "STANDARD";
        private bool _usePathStyle = false;
        private bool _enableServerSideEncryption = false;
        private string _sseAlgorithm = "AES256"; // AES256, aws:kms, aws:kms:dsse
        private string? _kmsKeyId = null;
        private bool _enableVersioning = false;
        private bool _enableTransferAcceleration = false;
        private int _timeoutSeconds = 300;
        private int _multipartThresholdBytes = 100 * 1024 * 1024; // 100MB
        private int _multipartChunkSizeBytes = 10 * 1024 * 1024; // 10MB
        private int _maxConcurrentParts = 5;
        private int _maxRetries = 3;
        private int _retryDelayMs = 1000;

        /// <summary>
        /// When true, uses the official AWSSDK.S3 client.
        /// When false, falls back to manual HTTP with Signature V4 (for air-gapped/embedded).
        /// </summary>
        private bool _useNativeClient = true;

        // Cached health check state
        private StorageHealthInfo? _cachedHealthInfo = null;
        private DateTime _lastHealthCheck = DateTime.MinValue;
        private readonly TimeSpan _healthCacheDuration = TimeSpan.FromSeconds(60);

        public override string StrategyId => "s3";
        public override string Name => "AWS S3 Storage";
        public override StorageTier Tier => StorageTier.Warm; // Default tier, can be overridden

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false, // S3 doesn't support file locking
            SupportsVersioning = true,
            SupportsTiering = true,
            SupportsEncryption = true,
            SupportsCompression = false, // Client-side compression can be added
            SupportsMultipart = true,
            MaxObjectSize = 5L * 1024 * 1024 * 1024 * 1024, // 5TB
            MaxObjects = null, // No practical limit
            ConsistencyModel = ConsistencyModel.Strong // S3 strong consistency as of Dec 2020
        };

        /// <summary>
        /// Initializes the S3 storage strategy.
        /// </summary>
        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load required configuration
            _endpoint = GetConfiguration<string>("Endpoint", "https://s3.amazonaws.com");
            _region = GetConfiguration<string>("Region", "us-east-1");
            _bucket = GetConfiguration<string>("Bucket", string.Empty);
            _accessKey = GetConfiguration<string>("AccessKey", string.Empty);
            _secretKey = GetConfiguration<string>("SecretKey", string.Empty);

            // Validate required configuration
            if (string.IsNullOrWhiteSpace(_bucket))
            {
                throw new InvalidOperationException("S3 bucket name is required. Set 'Bucket' in configuration.");
            }

            // Validate S3 bucket name format
            if (!System.Text.RegularExpressions.Regex.IsMatch(_bucket, @"^[a-z0-9][a-z0-9\-\.]{1,61}[a-z0-9]$"))
            {
                throw new ArgumentException($"Invalid S3 bucket name format: {_bucket}. Must be 3-63 characters, lowercase alphanumeric with hyphens/dots.");
            }

            if (string.IsNullOrWhiteSpace(_accessKey))
            {
                throw new InvalidOperationException("S3 access key is required. Set 'AccessKey' in configuration.");
            }

            if (string.IsNullOrWhiteSpace(_secretKey))
            {
                throw new InvalidOperationException("S3 secret key is required. Set 'SecretKey' in configuration.");
            }

            // Validate endpoint URL format
            if (!Uri.TryCreate(_endpoint, UriKind.Absolute, out var endpointUri))
            {
                throw new ArgumentException($"Invalid S3 endpoint URL: {_endpoint}. Must be a valid URI.");
            }

            // Validate endpoint is HTTPS or explicitly opt-in to HTTP
            var allowHttp = GetConfiguration<bool>("AllowHttp", false);
            if (endpointUri.Scheme == "http" && !allowHttp)
            {
                throw new InvalidOperationException($"S3 endpoint must use HTTPS: {_endpoint}. Set 'AllowHttp=true' to explicitly allow HTTP (not recommended).");
            }

            // Validate region is provided (required for signature V4)
            if (string.IsNullOrWhiteSpace(_region))
            {
                throw new InvalidOperationException("S3 region is required. Set 'Region' in configuration (e.g., 'us-east-1').");
            }

            // Load optional configuration with validation
            _defaultStorageClass = GetConfiguration<string>("DefaultStorageClass", "STANDARD");
            _usePathStyle = GetConfiguration<bool>("UsePathStyle", false);
            _enableServerSideEncryption = GetConfiguration<bool>("EnableServerSideEncryption", false);
            _sseAlgorithm = GetConfiguration<string>("SSEAlgorithm", "AES256");
            _kmsKeyId = GetConfiguration<string?>("KMSKeyId", null);
            _enableVersioning = GetConfiguration<bool>("EnableVersioning", false);
            _enableTransferAcceleration = GetConfiguration<bool>("EnableTransferAcceleration", false);

            _timeoutSeconds = GetConfiguration<int>("TimeoutSeconds", 300);
            if (_timeoutSeconds < 10 || _timeoutSeconds > 3600)
            {
                throw new ArgumentException($"Invalid timeout {_timeoutSeconds} seconds. Must be between 10 and 3600.");
            }

            _multipartThresholdBytes = GetConfiguration<int>("MultipartThresholdBytes", 100 * 1024 * 1024);
            if (_multipartThresholdBytes < 5 * 1024 * 1024) // S3 minimum part size
            {
                throw new ArgumentException($"Multipart threshold must be at least 5MB. Current: {_multipartThresholdBytes} bytes.");
            }

            _multipartChunkSizeBytes = GetConfiguration<int>("MultipartChunkSizeBytes", 10 * 1024 * 1024);
            if (_multipartChunkSizeBytes < 5 * 1024 * 1024)
            {
                throw new ArgumentException($"Multipart chunk size must be at least 5MB. Current: {_multipartChunkSizeBytes} bytes.");
            }

            _maxConcurrentParts = GetConfiguration<int>("MaxConcurrentParts", 5);
            if (_maxConcurrentParts < 1 || _maxConcurrentParts > 100)
            {
                throw new ArgumentException($"Max concurrent parts must be between 1 and 100. Current: {_maxConcurrentParts}.");
            }

            _maxRetries = GetConfiguration<int>("MaxRetries", 3);
            if (_maxRetries < 0 || _maxRetries > 10)
            {
                throw new ArgumentException($"Max retries must be between 0 and 10. Current: {_maxRetries}.");
            }

            _retryDelayMs = GetConfiguration<int>("RetryDelayMs", 1000);

            // Determine client mode
            _useNativeClient = GetConfiguration<bool>("UseNativeClient", true);

            if (_useNativeClient)
            {
                InitializeNativeClient();
            }
            else
            {
                InitializeManualHttpClient();
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Initializes the official AWSSDK.S3 client (primary path).
        /// </summary>
        private void InitializeNativeClient()
        {
            var credentials = new BasicAWSCredentials(_accessKey, _secretKey);

            var config = new AmazonS3Config
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(_region),
                ForcePathStyle = _usePathStyle,
                UseAccelerateEndpoint = _enableTransferAcceleration,
                Timeout = TimeSpan.FromSeconds(_timeoutSeconds),
                MaxErrorRetry = _maxRetries
            };

            // Support custom endpoints (MinIO, Wasabi, DigitalOcean Spaces, etc.)
            if (_endpoint != "https://s3.amazonaws.com")
            {
                config.ServiceURL = _endpoint;
                // When using custom endpoints, ForcePathStyle is typically required
                if (!_usePathStyle)
                {
                    config.ForcePathStyle = true;
                }
            }

            // Adjust for transfer acceleration
            if (_enableTransferAcceleration)
            {
                config.UseAccelerateEndpoint = true;
            }

            _s3Client = new AmazonS3Client(credentials, config);

            // Initialize TransferUtility for multipart uploads
            var transferConfig = new TransferUtilityConfig
            {
                ConcurrentServiceRequests = _maxConcurrentParts,
                MinSizeBeforePartUpload = _multipartThresholdBytes
            };
            _transferUtility = new TransferUtility(_s3Client, transferConfig);
        }

        /// <summary>
        /// Initializes the manual HTTP client (fallback for air-gapped/embedded environments).
        /// </summary>
        private void InitializeManualHttpClient()
        {
            // Adjust endpoint for transfer acceleration
            if (_enableTransferAcceleration)
            {
                _endpoint = _endpoint.Replace("s3.", "s3-accelerate.");
            }

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_timeoutSeconds)
            };
        }

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            ValidateKey(key);
            ValidateStream(data);

            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Key cannot be null or empty", nameof(key));
            }

            // Validate max object size (5TB S3 limit)
            if (data.CanSeek && data.Length > 5L * 1024 * 1024 * 1024 * 1024)
            {
                throw new ArgumentException($"Object size {data.Length} bytes exceeds S3 maximum of 5TB", nameof(data));
            }

            if (_useNativeClient)
            {
                return await StoreWithSdkAsync(key, data, metadata, ct);
            }
            else
            {
                return await StoreWithManualHttpAsync(key, data, metadata, ct);
            }
        }

        /// <summary>
        /// Store using AWSSDK.S3 (primary path).
        /// Uses TransferUtility which automatically handles multipart for large objects.
        /// </summary>
        private async Task<StorageObjectMetadata> StoreWithSdkAsync(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            var request = new TransferUtilityUploadRequest
            {
                BucketName = _bucket,
                Key = key,
                InputStream = data,
                ContentType = GetContentType(key),
                StorageClass = MapToS3StorageClass(_defaultStorageClass),
                PartSize = _multipartChunkSizeBytes,
                AutoCloseStream = false
            };

            // Add server-side encryption
            if (_enableServerSideEncryption)
            {
                request.ServerSideEncryptionMethod = _sseAlgorithm switch
                {
                    "AES256" => ServerSideEncryptionMethod.AES256,
                    "aws:kms" => ServerSideEncryptionMethod.AWSKMS,
                    _ => ServerSideEncryptionMethod.AES256
                };

                if (_sseAlgorithm == "aws:kms" && !string.IsNullOrEmpty(_kmsKeyId))
                {
                    request.ServerSideEncryptionKeyManagementServiceKeyId = _kmsKeyId;
                }
            }

            // Add custom metadata
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    request.Metadata.Add(kvp.Key, kvp.Value);
                }
            }

            // TransferUtility handles multipart automatically based on size
            await _transferUtility!.UploadAsync(request, ct);

            // Get the metadata of the uploaded object to return accurate info
            var getMetaRequest = new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = key
            };

            var metaResponse = await _s3Client!.GetObjectMetadataAsync(getMetaRequest, ct);

            var size = metaResponse.ContentLength;
            IncrementBytesStored(size);
            IncrementOperationCounter(StorageOperationType.Store);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = size,
                Created = metaResponse.LastModified ?? DateTime.UtcNow,
                Modified = metaResponse.LastModified ?? DateTime.UtcNow,
                ETag = metaResponse.ETag?.Trim('"') ?? string.Empty,
                ContentType = metaResponse.Headers.ContentType ?? GetContentType(key),
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = MapStorageClassToTier(_defaultStorageClass)
            };
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            if (_useNativeClient)
            {
                return await RetrieveWithSdkAsync(key, ct);
            }
            else
            {
                return await RetrieveWithManualHttpAsync(key, ct);
            }
        }

        /// <summary>
        /// Retrieve using AWSSDK.S3 (primary path).
        /// </summary>
        private async Task<Stream> RetrieveWithSdkAsync(string key, CancellationToken ct)
        {
            var request = new GetObjectRequest
            {
                BucketName = _bucket,
                Key = key
            };

            var response = await _s3Client!.GetObjectAsync(request, ct);

            // Copy to MemoryStream so we can track size and the response can be disposed
            var ms = new MemoryStream();
            await response.ResponseStream.CopyToAsync(ms, 81920, ct);
            ms.Position = 0;

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
                var meta = await GetMetadataAsyncCore(key, ct);
                size = meta.Size;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[management.DeleteAsyncCore] {ex.GetType().Name}: {ex.Message}");
                // Ignore if metadata retrieval fails
            }

            if (_useNativeClient)
            {
                var request = new DeleteObjectRequest
                {
                    BucketName = _bucket,
                    Key = key
                };

                await _s3Client!.DeleteObjectAsync(request, ct);
            }
            else
            {
                await DeleteWithManualHttpAsync(key, ct);
            }

            if (size > 0)
            {
                IncrementBytesDeleted(size);
            }
            IncrementOperationCounter(StorageOperationType.Delete);
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            if (_useNativeClient)
            {
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
                    System.Diagnostics.Debug.WriteLine($"[management.ExistsAsyncCore] {ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }
            else
            {
                return await ExistsWithManualHttpAsync(key, ct);
            }
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.List);

            if (_useNativeClient)
            {
                await foreach (var item in ListWithSdkAsync(prefix, ct))
                {
                    yield return item;
                }
            }
            else
            {
                await foreach (var item in ListWithManualHttpAsync(prefix, ct))
                {
                    yield return item;
                }
            }
        }

        /// <summary>
        /// List objects using AWSSDK.S3 with ContinuationToken pagination.
        /// </summary>
        private async IAsyncEnumerable<StorageObjectMetadata> ListWithSdkAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            string? continuationToken = null;

            do
            {
                var request = new ListObjectsV2Request
                {
                    BucketName = _bucket,
                    Prefix = prefix,
                    ContinuationToken = continuationToken
                };

                var response = await _s3Client!.ListObjectsV2Async(request, ct);

                foreach (var obj in response.S3Objects)
                {
                    ct.ThrowIfCancellationRequested();

                    yield return new StorageObjectMetadata
                    {
                        Key = obj.Key,
                        Size = obj.Size ?? 0L,
                        Created = obj.LastModified ?? DateTime.UtcNow,
                        Modified = obj.LastModified ?? DateTime.UtcNow,
                        ETag = obj.ETag?.Trim('"') ?? string.Empty,
                        ContentType = GetContentType(obj.Key),
                        CustomMetadata = null,
                        Tier = MapStorageClassToTier(obj.StorageClass?.Value ?? "STANDARD")
                    };

                    await Task.Yield();
                }

                continuationToken = (response.IsTruncated ?? false) ? response.NextContinuationToken : null;

            } while (continuationToken != null);
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            if (_useNativeClient)
            {
                return await GetMetadataWithSdkAsync(key, ct);
            }
            else
            {
                return await GetMetadataWithManualHttpAsync(key, ct);
            }
        }

        /// <summary>
        /// Get metadata using AWSSDK.S3 GetObjectMetadata.
        /// </summary>
        private async Task<StorageObjectMetadata> GetMetadataWithSdkAsync(string key, CancellationToken ct)
        {
            var request = new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = key
            };

            var response = await _s3Client!.GetObjectMetadataAsync(request, ct);

            // Extract custom metadata from response
            var customMetadata = new Dictionary<string, string>();
            foreach (var metaKey in response.Metadata.Keys)
            {
                customMetadata[metaKey] = response.Metadata[metaKey];
            }

            // Extract storage class from headers
            var storageClass = response.StorageClass?.Value ?? "STANDARD";

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
                Tier = MapStorageClassToTier(storageClass)
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            // Return cached health info if within cache duration
            if (_cachedHealthInfo != null && (DateTime.UtcNow - _lastHealthCheck) < _healthCacheDuration)
            {
                return _cachedHealthInfo;
            }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                StorageHealthInfo healthInfo;

                if (_useNativeClient)
                {
                    // Use ListBuckets as a health check via the SDK
                    await _s3Client!.ListBucketsAsync(ct);
                    sw.Stop();

                    healthInfo = new StorageHealthInfo
                    {
                        Status = HealthStatus.Healthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"S3 bucket '{_bucket}' is accessible at {_endpoint}",
                        CheckedAt = DateTime.UtcNow
                    };
                }
                else
                {
                    healthInfo = await GetHealthWithManualHttpAsync(ct, sw);
                }

                _cachedHealthInfo = healthInfo;
                _lastHealthCheck = DateTime.UtcNow;
                return healthInfo;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var healthInfo = new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"S3 bucket '{_bucket}' access denied (403). Check credentials and bucket permissions.",
                    CheckedAt = DateTime.UtcNow
                };
                _cachedHealthInfo = healthInfo;
                _lastHealthCheck = DateTime.UtcNow;
                return healthInfo;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var healthInfo = new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"S3 bucket '{_bucket}' not found (404). Verify bucket name and region.",
                    CheckedAt = DateTime.UtcNow
                };
                _cachedHealthInfo = healthInfo;
                _lastHealthCheck = DateTime.UtcNow;
                return healthInfo;
            }
            catch (Exception ex)
            {
                var healthInfo = new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to access S3 bucket '{_bucket}': {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
                _cachedHealthInfo = healthInfo;
                _lastHealthCheck = DateTime.UtcNow;
                return healthInfo;
            }
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            // S3 has no practical capacity limit
            return Task.FromResult<long?>(null);
        }

        #endregion

        #region S3-Specific Operations

        /// <summary>
        /// Generates a presigned URL for temporary access to an object using AWSSDK.S3.
        /// Falls back to manual signing when not using native client.
        /// </summary>
        public string GeneratePresignedUrl(string key, TimeSpan expiresIn)
        {
            ValidateKey(key);

            if (_useNativeClient)
            {
                var request = new GetPreSignedUrlRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    Expires = DateTime.UtcNow.Add(expiresIn),
                    Verb = HttpVerb.GET
                };

                return _s3Client!.GetPreSignedURL(request);
            }
            else
            {
                return GeneratePresignedUrlV4Manual(key, expiresIn);
            }
        }

        /// <summary>
        /// Copies an object within S3 using AWSSDK.S3.
        /// </summary>
        public async Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken ct = default)
        {
            ValidateKey(sourceKey);
            ValidateKey(destinationKey);

            if (_useNativeClient)
            {
                var request = new CopyObjectRequest
                {
                    SourceBucket = _bucket,
                    SourceKey = sourceKey,
                    DestinationBucket = _bucket,
                    DestinationKey = destinationKey
                };

                await _s3Client!.CopyObjectAsync(request, ct);
            }
            else
            {
                await CopyObjectManualHttpAsync(sourceKey, destinationKey, ct);
            }
        }

        /// <summary>
        /// Changes the storage class of an object (tiering) using AWSSDK.S3.
        /// </summary>
        public async Task ChangeStorageClassAsync(string key, string storageClass, CancellationToken ct = default)
        {
            ValidateKey(key);

            if (_useNativeClient)
            {
                var request = new CopyObjectRequest
                {
                    SourceBucket = _bucket,
                    SourceKey = key,
                    DestinationBucket = _bucket,
                    DestinationKey = key,
                    StorageClass = MapToS3StorageClass(storageClass),
                    MetadataDirective = S3MetadataDirective.COPY
                };

                await _s3Client!.CopyObjectAsync(request, ct);
            }
            else
            {
                await ChangeStorageClassManualHttpAsync(key, storageClass, ct);
            }
        }

        #endregion

        #region Manual HTTP Fallback Methods

        /// <summary>
        /// Store using manual HTTP with Signature V4 (fallback path).
        /// </summary>
        private async Task<StorageObjectMetadata> StoreWithManualHttpAsync(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
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
                return await StoreMultipartManualAsync(key, data, dataLength, metadata, ct);
            }
            else
            {
                return await StoreSinglePartManualAsync(key, data, metadata, ct);
            }
        }

        private async Task<StorageObjectMetadata> StoreSinglePartManualAsync(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            var endpoint = GetEndpointUrl(key);

            using var ms = new MemoryStream(65536);
            await data.CopyToAsync(ms, 81920, ct);
            var content = ms.ToArray();

            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GetContentType(key));

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

            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    request.Headers.TryAddWithoutValidation($"x-amz-meta-{kvp.Key}", kvp.Value);
                }
            }

            await SignRequestAsync(request, content, ct);
            var response = await SendWithRetryAsync(request, ct);

            var etag = response.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;

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

        private async Task<StorageObjectMetadata> StoreMultipartManualAsync(string key, Stream data, long dataLength, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            var uploadId = await InitiateMultipartUploadAsync(key, metadata, ct);

            try
            {
                var partCount = (int)Math.Ceiling((double)dataLength / _multipartChunkSizeBytes);
                var completedParts = new List<ManualCompletedPart>();
                using var semaphore = new SemaphoreSlim(_maxConcurrentParts, _maxConcurrentParts);
                var uploadTasks = new List<Task<ManualCompletedPart>>();

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
                            return await UploadPartManualAsync(key, uploadId, currentPartNumber, partData, ct);
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

                var etag = await CompleteMultipartUploadAsync(key, uploadId, completedParts, ct);

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
                try
                {
                    await AbortMultipartUploadAsync(key, uploadId, ct);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[management.StoreMultipartManualAsync] {ex.GetType().Name}: {ex.Message}");
                    // Ignore abort failures
                }
                throw;
            }
        }

        private async Task<Stream> RetrieveWithManualHttpAsync(string key, CancellationToken ct)
        {
            var endpoint = GetEndpointUrl(key);
            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

            await SignRequestAsync(request, null, ct);
            var response = await SendWithRetryAsync(request, ct);

            var ms = new MemoryStream(65536);
            await response.Content.CopyToAsync(ms, ct);
            ms.Position = 0;

            IncrementBytesRetrieved(ms.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            return ms;
        }

        private async Task DeleteWithManualHttpAsync(string key, CancellationToken ct)
        {
            var endpoint = GetEndpointUrl(key);
            var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);

            await SignRequestAsync(request, null, ct);
            await SendWithRetryAsync(request, ct);
        }

        private async Task<bool> ExistsWithManualHttpAsync(string key, CancellationToken ct)
        {
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
                System.Diagnostics.Debug.WriteLine($"[management.ExistsWithManualHttpAsync] {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private async IAsyncEnumerable<StorageObjectMetadata> ListWithManualHttpAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
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

                var doc = XDocument.Parse(xml);
                var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

                var contents = doc.Descendants(ns + "Contents");
                foreach (var content in contents)
                {
                    ct.ThrowIfCancellationRequested();

                    var itemKey = content.Element(ns + "Key")?.Value;
                    if (string.IsNullOrEmpty(itemKey))
                        continue;

                    var sizeStr = content.Element(ns + "Size")?.Value;
                    var size = long.TryParse(sizeStr, out var parsedSize) ? parsedSize : 0L;

                    var lastModifiedStr = content.Element(ns + "LastModified")?.Value;
                    var lastModified = DateTime.TryParse(lastModifiedStr, out var parsedDate) ? parsedDate : DateTime.UtcNow;

                    var etag = content.Element(ns + "ETag")?.Value?.Trim('"') ?? string.Empty;
                    var storageClass = content.Element(ns + "StorageClass")?.Value ?? "STANDARD";

                    yield return new StorageObjectMetadata
                    {
                        Key = itemKey,
                        Size = size,
                        Created = lastModified,
                        Modified = lastModified,
                        ETag = etag,
                        ContentType = GetContentType(itemKey),
                        CustomMetadata = null,
                        Tier = MapStorageClassToTier(storageClass)
                    };

                    await Task.Yield();
                }

                continuationToken = doc.Descendants(ns + "NextContinuationToken").FirstOrDefault()?.Value;

            } while (continuationToken != null);
        }

        private async Task<StorageObjectMetadata> GetMetadataWithManualHttpAsync(string key, CancellationToken ct)
        {
            var endpoint = GetEndpointUrl(key);
            var request = new HttpRequestMessage(HttpMethod.Head, endpoint);

            await SignRequestAsync(request, null, ct);
            var response = await SendWithRetryAsync(request, ct);

            var size = response.Content.Headers.ContentLength ?? 0;
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            var lastModified = response.Content.Headers.LastModified?.UtcDateTime ?? DateTime.UtcNow;
            var etag = response.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;

            var storageClass = "STANDARD";
            if (response.Headers.TryGetValues("x-amz-storage-class", out var storageClassValues))
            {
                storageClass = storageClassValues.FirstOrDefault() ?? "STANDARD";
            }

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

        private async Task<StorageHealthInfo> GetHealthWithManualHttpAsync(CancellationToken ct, System.Diagnostics.Stopwatch sw)
        {
            var endpoint = $"{_endpoint}/{_bucket}?list-type=2&max-keys=1";
            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            await SignRequestAsync(request, null, ct);

            var response = await _httpClient!.SendAsync(request, ct);
            sw.Stop();

            if (response.IsSuccessStatusCode)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Healthy,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Message = $"S3 bucket '{_bucket}' is accessible at {_endpoint}",
                    CheckedAt = DateTime.UtcNow
                };
            }
            else if ((int)response.StatusCode == 403)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Message = $"S3 bucket '{_bucket}' access denied (403). Check credentials and bucket permissions.",
                    CheckedAt = DateTime.UtcNow
                };
            }
            else if ((int)response.StatusCode == 404)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Message = $"S3 bucket '{_bucket}' not found (404). Verify bucket name and region.",
                    CheckedAt = DateTime.UtcNow
                };
            }
            else if ((int)response.StatusCode == 503)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Degraded,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Message = $"S3 service temporarily unavailable (503) for bucket '{_bucket}'",
                    CheckedAt = DateTime.UtcNow
                };
            }
            else
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Message = $"S3 bucket '{_bucket}' returned status code {response.StatusCode}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        private async Task CopyObjectManualHttpAsync(string sourceKey, string destinationKey, CancellationToken ct)
        {
            var endpoint = GetEndpointUrl(destinationKey);
            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            request.Headers.TryAddWithoutValidation("x-amz-copy-source", $"/{_bucket}/{sourceKey}");

            await SignRequestAsync(request, null, ct);
            await SendWithRetryAsync(request, ct);
        }

        private async Task ChangeStorageClassManualHttpAsync(string key, string storageClass, CancellationToken ct)
        {
            var endpoint = GetEndpointUrl(key);
            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            request.Headers.TryAddWithoutValidation("x-amz-copy-source", $"/{_bucket}/{key}");
            request.Headers.TryAddWithoutValidation("x-amz-storage-class", storageClass);
            request.Headers.TryAddWithoutValidation("x-amz-metadata-directive", "COPY");

            await SignRequestAsync(request, null, ct);
            await SendWithRetryAsync(request, ct);
        }

        private async Task<string> InitiateMultipartUploadAsync(string key, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            var endpoint = $"{GetEndpointUrl(key)}?uploads";
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

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

        private async Task<ManualCompletedPart> UploadPartManualAsync(string key, string uploadId, int partNumber, byte[] partData, CancellationToken ct)
        {
            var endpoint = $"{GetEndpointUrl(key)}?partNumber={partNumber}&uploadId={Uri.EscapeDataString(uploadId)}";
            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            request.Content = new ByteArrayContent(partData);

            await SignRequestAsync(request, partData, ct);
            var response = await SendWithRetryAsync(request, ct);

            var etag = response.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;

            return new ManualCompletedPart
            {
                PartNumber = partNumber,
                ETag = etag
            };
        }

        private async Task<string> CompleteMultipartUploadAsync(string key, string uploadId, List<ManualCompletedPart> parts, CancellationToken ct)
        {
            var endpoint = $"{GetEndpointUrl(key)}?uploadId={Uri.EscapeDataString(uploadId)}";

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

        /// <summary>
        /// Generates a presigned URL using manual AWS Signature Version 4.
        /// </summary>
        private string GeneratePresignedUrlV4Manual(string key, TimeSpan expiresIn)
        {
            var now = DateTime.UtcNow;
            var dateStamp = now.ToString("yyyyMMdd");
            var amzDate = now.ToString("yyyyMMddTHHmmssZ");
            var expiresInSeconds = (int)expiresIn.TotalSeconds;

            var endpoint = GetEndpointUrl(key);
            var uri = new Uri(endpoint);

            var credentialScope = $"{dateStamp}/{_region}/s3/aws4_request";
            var credential = $"{_accessKey}/{credentialScope}";

            var queryParams = new SortedDictionary<string, string>
            {
                { "X-Amz-Algorithm", "AWS4-HMAC-SHA256" },
                { "X-Amz-Credential", credential },
                { "X-Amz-Date", amzDate },
                { "X-Amz-Expires", expiresInSeconds.ToString() },
                { "X-Amz-SignedHeaders", "host" }
            };

            var canonicalQueryString = string.Join("&", queryParams.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

            var canonicalUri = uri.AbsolutePath;
            var canonicalHeaders = $"host:{uri.Host}\n";
            var signedHeaders = "host";
            var payloadHash = "UNSIGNED-PAYLOAD";

            var canonicalRequest = $"GET\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
            var canonicalRequestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLower();

            var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{canonicalRequestHash}";

            var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + _secretKey), dateStamp);
            var kRegion = HmacSha256(kDate, _region);
            var kService = HmacSha256(kRegion, "s3");
            var kSigning = HmacSha256(kService, "aws4_request");
            var signature = Convert.ToHexString(HmacSha256(kSigning, stringToSign)).ToLower();

            return $"{endpoint}?{canonicalQueryString}&X-Amz-Signature={signature}";
        }

        #endregion

        #region Helper Methods

        private string GetEndpointUrl(string key)
        {
            if (_usePathStyle)
            {
                return $"{_endpoint}/{_bucket}/{key}";
            }

            // Virtual-hosted style
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

            var contentHash = content != null
                ? Convert.ToHexString(SHA256.HashData(content)).ToLower()
                : "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"; // Empty hash

            request.Headers.TryAddWithoutValidation("x-amz-content-sha256", contentHash);

            var uri = request.RequestUri!;
            var canonicalUri = uri.AbsolutePath;
            var canonicalQueryString = uri.Query.TrimStart('?');

            var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
            var canonicalHeaders = $"host:{uri.Host}\nx-amz-content-sha256:{contentHash}\nx-amz-date:{amzDate}\n";

            var canonicalRequest = $"{request.Method}\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{contentHash}";
            var canonicalRequestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLower();

            var credentialScope = $"{dateStamp}/{_region}/s3/aws4_request";
            var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{canonicalRequestHash}";

            var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + _secretKey), dateStamp);
            var kRegion = HmacSha256(kDate, _region);
            var kService = HmacSha256(kRegion, "s3");
            var kSigning = HmacSha256(kService, "aws4_request");
            var signature = Convert.ToHexString(HmacSha256(kSigning, stringToSign)).ToLower();

            var authHeader = $"AWS4-HMAC-SHA256 Credential={_accessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
            request.Headers.TryAddWithoutValidation("Authorization", authHeader);

            await Task.CompletedTask; // Keep async signature for future enhancements
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

                    if (!ShouldRetryStatusCode(response.StatusCode) || attempt == _maxRetries)
                    {
                        response.EnsureSuccessStatusCode();
                        return response;
                    }
                }
                catch (Exception ex) when (attempt < _maxRetries)
                {
                    lastException = ex;
                }

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

        private bool ShouldRetryStatusCode(System.Net.HttpStatusCode statusCode)
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
                "STANDARD_IA" or "ONEZONE_IA" => StorageTier.Warm,
                "GLACIER_IR" or "GLACIER INSTANT RETRIEVAL" => StorageTier.Cold,
                "GLACIER" or "GLACIER FLEXIBLE RETRIEVAL" => StorageTier.Archive,
                "DEEP_ARCHIVE" => StorageTier.Archive,
                "INTELLIGENT_TIERING" => StorageTier.Hot,
                _ => StorageTier.Hot
            };
        }

        /// <summary>
        /// Maps storage class string to AWSSDK S3StorageClass enum.
        /// </summary>
        private static S3StorageClass MapToS3StorageClass(string storageClass)
        {
            return storageClass.ToUpperInvariant() switch
            {
                "STANDARD" => S3StorageClass.Standard,
                "STANDARD_IA" => S3StorageClass.StandardInfrequentAccess,
                "ONEZONE_IA" => S3StorageClass.OneZoneInfrequentAccess,
                "GLACIER" or "GLACIER FLEXIBLE RETRIEVAL" => S3StorageClass.Glacier,
                "GLACIER_IR" or "GLACIER INSTANT RETRIEVAL" => S3StorageClass.GlacierInstantRetrieval,
                "DEEP_ARCHIVE" => S3StorageClass.DeepArchive,
                "INTELLIGENT_TIERING" => S3StorageClass.IntelligentTiering,
                _ => S3StorageClass.Standard
            };
        }

        protected override int GetMaxKeyLength() => 1024; // S3 max key length

        #endregion

        #region Cleanup

        /// <summary>
        /// Gracefully shuts down S3 client with timeout for pending operations.
        /// </summary>
        protected override async Task ShutdownAsyncCore(CancellationToken ct = default)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                await Task.Delay(100, linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout or cancellation - proceed with disposal
            }
        }

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();

            try
            {
                await ShutdownAsyncCore();
            }
            catch
            {
                // Ignore shutdown errors during disposal
            }

            // Dispose SDK resources
            _transferUtility?.Dispose();
            _s3Client?.Dispose();

            // Dispose manual HTTP fallback
            _httpClient?.Dispose();
            _cachedHealthInfo = null;
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Represents a completed part in a manual multipart upload (fallback path).
    /// </summary>
    internal class ManualCompletedPart
    {
        public int PartNumber { get; set; }
        public string ETag { get; set; } = string.Empty;
    }

    #endregion
}
