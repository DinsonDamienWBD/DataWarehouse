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
    /// OVH Object Storage strategy using S3-compatible API.
    /// Full production features include:
    /// - S3-compatible API via AWS SDK (100% S3 API compatible)
    /// - European infrastructure with GDPR compliance
    /// - Multi-region support across Europe and North America
    /// - High durability (99.999999999% - 11 nines)
    /// - Server-side encryption (AES-256)
    /// - Large file support with multipart uploads
    /// - Competitive pricing (€0.01/GB/month storage)
    /// - Storage classes: STANDARD, STANDARD_IA
    /// - Presigned URLs for temporary access
    /// - Integrated with OVH Public Cloud ecosystem
    /// - Automatic retry with exponential backoff
    /// - CORS configuration for web applications
    /// - Lifecycle policies for automatic expiration
    /// </summary>
    /// <remarks>
    /// OVH Object Storage provides S3-compatible object storage with European and North American presence.
    /// Optimized for storing backups, archives, media files, static content, and application data.
    ///
    /// Endpoint Format: s3.{region}.cloud.ovh.net
    /// Supported Regions:
    /// - gra: Gravelines, France (Primary EU region, Northwest)
    /// - sbg: Strasbourg, France (EU region, Northeast)
    /// - bhs: Beauharnois, Canada (North America region)
    /// - de: Frankfurt, Germany (EU region, Central)
    /// - uk: London, United Kingdom (EU region)
    /// - waw: Warsaw, Poland (EU region, Central-East)
    ///
    /// Configuration Requirements:
    /// - Region: OVH Object Storage region identifier (e.g., "gra", "sbg", "bhs")
    /// - Bucket: Bucket name (globally unique within OVH)
    /// - AccessKey: OVH Access Key (S3 credentials)
    /// - SecretKey: OVH Secret Key (S3 credentials)
    ///
    /// Storage Classes:
    /// - STANDARD: Hot storage for frequently accessed data (default)
    ///   * Low latency, high throughput
    ///   * €0.01/GB/month storage
    ///   * No retrieval fees
    ///   * Multi-AZ redundancy
    ///
    /// - STANDARD_IA: Infrequent access storage
    ///   * Lower cost for infrequently accessed data
    ///   * €0.006/GB/month storage
    ///   * Small retrieval fee applies
    ///   * Multi-AZ redundancy
    ///
    /// Features:
    /// - Automatic server-side encryption (AES-256)
    /// - S3-compatible ACLs (Private, Public Read, etc.)
    /// - Presigned URLs for temporary access (up to 7 days)
    /// - Lifecycle policies for automatic tier transition and expiration
    /// - CORS support for web applications
    /// - Multipart uploads for large files (>100MB recommended)
    /// - Strong consistency (read-after-write)
    /// - Object tagging for categorization
    /// - Cross-region replication support
    /// - Integration with OVH Public Cloud services
    ///
    /// Performance Characteristics:
    /// - Multipart upload recommended for files >100MB
    /// - Maximum object size: 5TB
    /// - Minimum part size: 5MB
    /// - Maximum part size: 5GB
    /// - Maximum parts per upload: 10,000
    /// - Strong read-after-write consistency
    /// - Low latency within European and North American regions
    ///
    /// Pricing Model (as of 2024):
    /// - Storage STANDARD: €0.01/GB/month
    /// - Storage STANDARD_IA: €0.006/GB/month
    /// - Transfer: €0.01/GB egress (first 1TB free per month)
    /// - No API request charges
    /// - No ingress fees
    /// - No minimum storage duration for STANDARD
    ///
    /// Integration Benefits:
    /// - Seamless integration with OVH Public Cloud Instances
    /// - Optimized transfer within OVH network (no egress fees)
    /// - Managed through OVH Manager console
    /// - Unified billing with other OVH services
    /// - Support for S3-compatible tools (s3cmd, rclone, etc.)
    /// - API compatibility with AWS S3 SDKs
    /// - GDPR compliant European infrastructure
    /// - Multi-AZ redundancy across data centers
    /// - Anti-DDoS protection included
    /// </remarks>
    public class OvhObjectStorageStrategy : UltimateStorageStrategyBase
    {
        private AmazonS3Client? _s3Client;
        private string _endpoint = string.Empty;
        private string _region = "gra";
        private string _bucket = string.Empty;
        private string _accessKey = string.Empty;
        private string _secretKey = string.Empty;
        private string _storageClass = "STANDARD"; // STANDARD, STANDARD_IA
        private bool _enableServerSideEncryption = true; // Always recommended
        private int _timeoutSeconds = 300;
        private long _multipartThresholdBytes = 100 * 1024 * 1024; // 100MB
        private long _multipartPartSizeBytes = 100 * 1024 * 1024; // 100MB default
        private int _maxConcurrentParts = 10;
        private int _maxRetries = 3;
        private int _retryDelayMs = 1000;
        private S3CannedACL _defaultAcl = S3CannedACL.Private;
        private bool _enableCors = false;

        public override string StrategyId => "ovh-object-storage";
        public override string Name => "OVH Object Storage";
        public override StorageTier Tier => DetermineStorageTier();

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false,
            SupportsVersioning = false, // OVH Object Storage doesn't support versioning currently
            SupportsTiering = true, // STANDARD, STANDARD_IA
            SupportsEncryption = true, // Automatic AES-256
            SupportsCompression = false,
            SupportsMultipart = true,
            MaxObjectSize = 5L * 1024 * 1024 * 1024 * 1024, // 5TB
            MaxObjects = null, // No practical limit
            ConsistencyModel = ConsistencyModel.Strong
        };

        #region Initialization

        /// <summary>
        /// Initializes the OVH Object Storage strategy.
        /// </summary>
        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load required configuration
            _region = GetConfiguration<string>("Region")
                ?? throw new InvalidOperationException("OVH region is required. Set 'Region' (e.g., 'gra', 'sbg', 'bhs', 'de', 'uk', 'waw').");

            _bucket = GetConfiguration<string>("Bucket")
                ?? throw new InvalidOperationException("Bucket name is required. Set 'Bucket' in configuration.");

            _accessKey = GetConfiguration<string>("AccessKey")
                ?? throw new InvalidOperationException("OVH Access Key is required. Set 'AccessKey' in configuration.");

            _secretKey = GetConfiguration<string>("SecretKey")
                ?? throw new InvalidOperationException("OVH Secret Key is required. Set 'SecretKey' in configuration.");

            // Validate region
            var validRegions = new[] { "gra", "sbg", "bhs", "de", "uk", "waw" };
            if (!validRegions.Contains(_region.ToLowerInvariant()))
            {
                throw new InvalidOperationException(
                    $"Invalid OVH region '{_region}'. Valid regions: {string.Join(", ", validRegions)}");
            }

            // Build endpoint
            _endpoint = GetConfiguration<string>("Endpoint", $"s3.{_region}.cloud.ovh.net");

            // Load optional configuration
            _storageClass = GetConfiguration("StorageClass", "STANDARD").ToUpperInvariant();

            // Validate storage class
            var validStorageClasses = new[] { "STANDARD", "STANDARD_IA" };
            if (!validStorageClasses.Contains(_storageClass))
            {
                throw new InvalidOperationException(
                    $"Invalid OVH storage class '{_storageClass}'. Valid classes: {string.Join(", ", validStorageClasses)}");
            }

            _enableServerSideEncryption = GetConfiguration("EnableServerSideEncryption", true);
            _timeoutSeconds = GetConfiguration("TimeoutSeconds", 300);
            _multipartThresholdBytes = GetConfiguration("MultipartThresholdBytes", 100L * 1024 * 1024);
            _multipartPartSizeBytes = GetConfiguration("MultipartPartSizeBytes", 100L * 1024 * 1024);
            _maxConcurrentParts = GetConfiguration("MaxConcurrentParts", 10);
            _maxRetries = GetConfiguration("MaxRetries", 3);
            _retryDelayMs = GetConfiguration("RetryDelayMs", 1000);
            _enableCors = GetConfiguration("EnableCors", false);

            // Parse default ACL
            var defaultAclString = GetConfiguration("DefaultAcl", "Private");
            _defaultAcl = defaultAclString.ToLowerInvariant() switch
            {
                "private" => S3CannedACL.Private,
                "public-read" => S3CannedACL.PublicRead,
                "public-read-write" => S3CannedACL.PublicReadWrite,
                "authenticated-read" => S3CannedACL.AuthenticatedRead,
                _ => S3CannedACL.Private
            };

            // Validate multipart settings (S3 requirements)
            if (_multipartPartSizeBytes < 5 * 1024 * 1024)
            {
                throw new InvalidOperationException("OVH Object Storage multipart part size must be at least 5MB");
            }

            if (_multipartPartSizeBytes > 5L * 1024 * 1024 * 1024)
            {
                throw new InvalidOperationException("OVH Object Storage multipart part size cannot exceed 5GB");
            }

            // Create AWS S3 client configured for OVH Object Storage
            var s3Config = new AmazonS3Config
            {
                ServiceURL = $"https://{_endpoint}",
                AuthenticationRegion = _region,
                ForcePathStyle = false, // OVH uses virtual-hosted-style URLs
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

        /// <summary>
        /// Determines the storage tier based on the configured storage class.
        /// </summary>
        private StorageTier DetermineStorageTier()
        {
            return _storageClass switch
            {
                "STANDARD" => StorageTier.Hot,
                "STANDARD_IA" => StorageTier.Cold,
                _ => StorageTier.Hot
            };
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
                AutoCloseStream = false,
                CannedACL = _defaultAcl,
                StorageClass = MapStorageClassToS3(_storageClass)
            };

            // Add metadata
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    request.Metadata.Add(kvp.Key, kvp.Value);
                }
            }

            // Apply server-side encryption
            if (_enableServerSideEncryption)
            {
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
            }

            // Execute upload with retry
            var response = await ExecuteWithRetryAsync(async () =>
            {
                if (data.CanSeek)
                {
                    data.Position = 0;
                }
                return await _s3Client!.PutObjectAsync(request, ct);
            }, ct);

            // Get actual size
            var size = data.CanSeek ? data.Length : response.ContentLength;
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
                CannedACL = _defaultAcl,
                StorageClass = MapStorageClassToS3(_storageClass)
            };

            // Add metadata
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    initiateRequest.Metadata.Add(kvp.Key, kvp.Value);
                }
            }

            // Apply server-side encryption
            if (_enableServerSideEncryption)
            {
                initiateRequest.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
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
                catch
                {
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

            var response = await ExecuteWithRetryAsync(async () =>
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
            catch
            {
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
            catch
            {
                IncrementOperationCounter(StorageOperationType.Exists);
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
                    Message = $"OVH Object Storage bucket '{_bucket}' is accessible in region {_region} with storage class {_storageClass}",
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to access OVH Object Storage bucket '{_bucket}' in region {_region}: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            // OVH Object Storage has no practical capacity limit from client perspective
            return Task.FromResult<long?>(null);
        }

        #endregion

        #region OVH-Specific Operations

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
        /// Gets the direct URL for an object.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <returns>Direct URL.</returns>
        public string GetDirectUrl(string key)
        {
            ValidateKey(key);
            return $"https://{_bucket}.{_endpoint}/{key}";
        }

        /// <summary>
        /// Copies an object within the same bucket (server-side copy - no data transfer).
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
        /// Transitions an object to a different storage class.
        /// Allows moving data between STANDARD and STANDARD_IA tiers.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="targetStorageClass">Target storage class (STANDARD, STANDARD_IA).</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task TransitionStorageClassAsync(
            string key,
            string targetStorageClass,
            CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            var validStorageClasses = new[] { "STANDARD", "STANDARD_IA" };
            if (!validStorageClasses.Contains(targetStorageClass.ToUpperInvariant()))
            {
                throw new ArgumentException(
                    $"Invalid storage class '{targetStorageClass}'. Valid classes: {string.Join(", ", validStorageClasses)}",
                    nameof(targetStorageClass));
            }

            // Use server-side copy to transition storage class
            var request = new CopyObjectRequest
            {
                SourceBucket = _bucket,
                SourceKey = key,
                DestinationBucket = _bucket,
                DestinationKey = key,
                StorageClass = MapStorageClassToS3(targetStorageClass.ToUpperInvariant()),
                MetadataDirective = S3MetadataDirective.COPY // Preserve existing metadata
            };

            await ExecuteWithRetryAsync(async () =>
                await _s3Client!.CopyObjectAsync(request, ct), ct);
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
        /// Configures CORS settings for the bucket.
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
        /// Gets the current CORS configuration for the bucket.
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
        /// Removes CORS configuration from the bucket.
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
        /// Configures lifecycle rules for automatic object expiration and tier transitions.
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
        /// Gets the lifecycle configuration for the bucket.
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

        /// <summary>
        /// Deletes the lifecycle configuration from the bucket.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async Task DeleteLifecycleConfigurationAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            var request = new DeleteLifecycleConfigurationRequest
            {
                BucketName = _bucket
            };

            await ExecuteWithRetryAsync(async () =>
                await _s3Client!.DeleteLifecycleConfigurationAsync(request, ct), ct);
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
        /// Maps OVH storage class string to S3StorageClass enum.
        /// </summary>
        private static S3StorageClass MapStorageClassToS3(string storageClass)
        {
            return storageClass.ToUpperInvariant() switch
            {
                "STANDARD" => S3StorageClass.Standard,
                "STANDARD_IA" => S3StorageClass.StandardInfrequentAccess,
                _ => S3StorageClass.Standard
            };
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

        protected override int GetMaxKeyLength() => 1024; // OVH max object name length (S3-compatible)

        #endregion
    }
}
