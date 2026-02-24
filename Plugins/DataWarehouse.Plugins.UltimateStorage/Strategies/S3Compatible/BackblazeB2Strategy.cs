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
    /// Backblaze B2 Cloud Storage strategy using S3-compatible API.
    /// Full production features include:
    /// - S3-compatible API via AWS SDK
    /// - Large file support with multipart uploads (>100MB recommended, required >5GB)
    /// - Application keys with limited bucket/path scopes
    /// - File locking via B2 Object Lock (write-once-read-many compliance)
    /// - Lifecycle rules for automatic transitions and expiration
    /// - Server-side encryption (SSE-B2 with AES-256)
    /// - Cost-effective storage with free egress (first 1GB/day, then 3x storage)
    /// - High durability (99.999999999% - 11 nines)
    /// - Automatic retry with exponential backoff
    /// - Bucket versioning support
    /// - Compatible with B2 Native API features through S3 endpoint
    /// </summary>
    /// <remarks>
    /// Backblaze B2 S3-compatible API endpoint format: s3.{region}.backblazeb2.com
    /// Example regions: us-west-001, us-west-002, us-west-003, us-west-004, us-east-005, eu-central-003
    ///
    /// Configuration requirements:
    /// - Endpoint: Full S3-compatible endpoint (e.g., "s3.us-west-001.backblazeb2.com")
    /// - Region: B2 region identifier (e.g., "us-west-001")
    /// - Bucket: Bucket name
    /// - AccessKey: Application Key ID (or Master Application Key ID)
    /// - SecretKey: Application Key (or Master Application Key)
    ///
    /// Application Key Capabilities:
    /// - Can be scoped to specific buckets
    /// - Can be scoped to specific path prefixes
    /// - Can have limited permissions (read-only, write-only, etc.)
    /// - Supports time-limited expiration
    ///
    /// Object Lock (File Locking):
    /// - Must be enabled at bucket creation (cannot be added later)
    /// - Supports GOVERNANCE and COMPLIANCE retention modes
    /// - Legal hold for indefinite retention
    /// - Integrates with S3 Object Lock API
    ///
    /// Performance:
    /// - Multipart upload recommended for files >100MB
    /// - Required for files >5GB (B2 single file limit)
    /// - Maximum object size: 10TB (via multipart)
    /// - Maximum parts: 10,000
    /// - Part size: 5MB to 5GB
    /// </remarks>
    public class BackblazeB2Strategy : UltimateStorageStrategyBase
    {
        private AmazonS3Client? _s3Client;
        private string _endpoint = string.Empty;
        private string _region = "us-west-001";
        private string _bucket = string.Empty;
        private string _accessKey = string.Empty;
        private string _secretKey = string.Empty;
        private bool _enableServerSideEncryption = true; // B2 encrypts by default
        private bool _enableVersioning = false;
        private bool _enableObjectLock = false;
        private int _timeoutSeconds = 300;
        private long _multipartThresholdBytes = 100 * 1024 * 1024; // 100MB (B2 recommendation)
        private long _multipartPartSizeBytes = 100 * 1024 * 1024; // 100MB (B2 default, 5MB minimum)
        private int _maxConcurrentParts = 5;
        private int _maxRetries = 3;
        private int _retryDelayMs = 1000;
        private string? _objectLockRetentionMode = null; // GOVERNANCE or COMPLIANCE
        private int? _objectLockRetentionDays = null;

        public override string StrategyId => "backblaze-b2";
        public override string Name => "Backblaze B2 Cloud Storage";
        public override StorageTier Tier => StorageTier.Cold; // B2 is cost-optimized for cold storage

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = true, // Via Object Lock
            SupportsVersioning = true,
            SupportsTiering = false, // B2 has a single storage class
            SupportsEncryption = true, // Server-side encryption (SSE-B2)
            SupportsCompression = false, // Client-side can be added
            SupportsMultipart = true,
            MaxObjectSize = 10L * 1024 * 1024 * 1024 * 1024, // 10TB via multipart
            MaxObjects = null, // No practical limit
            ConsistencyModel = ConsistencyModel.Strong // Strong consistency
        };

        #region Initialization

        /// <summary>
        /// Initializes the Backblaze B2 storage strategy.
        /// </summary>
        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load required configuration
            _endpoint = GetConfiguration<string>("Endpoint")
                ?? throw new InvalidOperationException("B2 Endpoint is required. Set 'Endpoint' (e.g., 's3.us-west-001.backblazeb2.com').");

            _bucket = GetConfiguration<string>("Bucket")
                ?? throw new InvalidOperationException("B2 Bucket name is required. Set 'Bucket' in configuration.");

            _accessKey = GetConfiguration<string>("AccessKey")
                ?? throw new InvalidOperationException("B2 Application Key ID is required. Set 'AccessKey' in configuration.");

            _secretKey = GetConfiguration<string>("SecretKey")
                ?? throw new InvalidOperationException("B2 Application Key is required. Set 'SecretKey' in configuration.");

            // Load optional configuration
            _region = GetConfiguration("Region", "us-west-001");
            _enableServerSideEncryption = GetConfiguration("EnableServerSideEncryption", true);
            _enableVersioning = GetConfiguration("EnableVersioning", false);
            _enableObjectLock = GetConfiguration("EnableObjectLock", false);
            _timeoutSeconds = GetConfiguration("TimeoutSeconds", 300);
            _multipartThresholdBytes = GetConfiguration("MultipartThresholdBytes", 100L * 1024 * 1024);
            _multipartPartSizeBytes = GetConfiguration("MultipartPartSizeBytes", 100L * 1024 * 1024);
            _maxConcurrentParts = GetConfiguration("MaxConcurrentParts", 5);
            _maxRetries = GetConfiguration("MaxRetries", 3);
            _retryDelayMs = GetConfiguration("RetryDelayMs", 1000);
            _objectLockRetentionMode = GetConfiguration<string?>("ObjectLockRetentionMode", null);
            _objectLockRetentionDays = GetConfiguration<int?>("ObjectLockRetentionDays", null);

            // Validate multipart settings
            if (_multipartPartSizeBytes < 5 * 1024 * 1024)
            {
                throw new InvalidOperationException("B2 multipart part size must be at least 5MB");
            }

            if (_multipartPartSizeBytes > 5L * 1024 * 1024 * 1024)
            {
                throw new InvalidOperationException("B2 multipart part size cannot exceed 5GB");
            }

            // Validate Object Lock settings
            if (_objectLockRetentionMode != null &&
                _objectLockRetentionMode != "GOVERNANCE" &&
                _objectLockRetentionMode != "COMPLIANCE")
            {
                throw new InvalidOperationException("ObjectLockRetentionMode must be GOVERNANCE or COMPLIANCE");
            }

            // Create AWS S3 client configured for B2
            var s3Config = new AmazonS3Config
            {
                ServiceURL = $"https://{_endpoint}",
                AuthenticationRegion = _region,
                ForcePathStyle = false, // B2 S3 API uses virtual-hosted-style
                Timeout = TimeSpan.FromSeconds(_timeoutSeconds),
                MaxErrorRetry = _maxRetries,
                RetryMode = RequestRetryMode.Standard,
                UseHttp = false, // Always use HTTPS with B2
                ThrottleRetries = true
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

            // Apply server-side encryption (B2 encrypts by default, but be explicit)
            if (_enableServerSideEncryption)
            {
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
            }

            // Apply Object Lock if configured
            if (_enableObjectLock && _objectLockRetentionMode != null && _objectLockRetentionDays.HasValue)
            {
                request.ObjectLockMode = _objectLockRetentionMode == "GOVERNANCE"
                    ? ObjectLockMode.Governance
                    : ObjectLockMode.Compliance;
                request.ObjectLockRetainUntilDate = DateTime.UtcNow.AddDays(_objectLockRetentionDays.Value);
            }

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
                ContentType = GetContentType(key)
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

            // Apply Object Lock
            if (_enableObjectLock && _objectLockRetentionMode != null && _objectLockRetentionDays.HasValue)
            {
                initiateRequest.ObjectLockMode = _objectLockRetentionMode == "GOVERNANCE"
                    ? ObjectLockMode.Governance
                    : ObjectLockMode.Compliance;
                initiateRequest.ObjectLockRetainUntilDate = DateTime.UtcNow.AddDays(_objectLockRetentionDays.Value);
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
                    System.Diagnostics.Debug.WriteLine($"[BackblazeB2Strategy.StoreMultipartAsync] {ex.GetType().Name}: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"[BackblazeB2Strategy.DeleteAsyncCore] {ex.GetType().Name}: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"[BackblazeB2Strategy.ExistsAsyncCore] {ex.GetType().Name}: {ex.Message}");
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
                    Message = $"Backblaze B2 bucket '{_bucket}' is accessible at {_endpoint}",
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to access B2 bucket '{_bucket}' at {_endpoint}: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            // B2 has no practical capacity limit from client perspective
            return Task.FromResult<long?>(null);
        }

        #endregion

        #region B2-Specific Operations

        /// <summary>
        /// Generates a presigned URL for temporary access to an object.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="expiresIn">How long the URL should be valid (max 7 days for B2).</param>
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
        /// Sets Object Lock retention on an object (requires Object Lock enabled on bucket).
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="mode">Retention mode (GOVERNANCE or COMPLIANCE).</param>
        /// <param name="retainUntilDate">Date until which the object is locked.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task SetObjectLockRetentionAsync(
            string key,
            string mode,
            DateTime retainUntilDate,
            CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            if (mode != "GOVERNANCE" && mode != "COMPLIANCE")
            {
                throw new ArgumentException("Retention mode must be GOVERNANCE or COMPLIANCE", nameof(mode));
            }

            var request = new PutObjectRetentionRequest
            {
                BucketName = _bucket,
                Key = key,
                Retention = new ObjectLockRetention
                {
                    Mode = mode == "GOVERNANCE" ? ObjectLockRetentionMode.Governance : ObjectLockRetentionMode.Compliance,
                    RetainUntilDate = retainUntilDate
                }
            };

            await ExecuteWithRetryAsync(async () =>
                await _s3Client!.PutObjectRetentionAsync(request, ct), ct);
        }

        /// <summary>
        /// Sets or removes Legal Hold on an object (requires Object Lock enabled on bucket).
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="enabled">True to enable legal hold, false to disable.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task SetObjectLegalHoldAsync(
            string key,
            bool enabled,
            CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            var request = new PutObjectLegalHoldRequest
            {
                BucketName = _bucket,
                Key = key,
                LegalHold = new ObjectLockLegalHold
                {
                    Status = enabled ? ObjectLockLegalHoldStatus.On : ObjectLockLegalHoldStatus.Off
                }
            };

            await ExecuteWithRetryAsync(async () =>
                await _s3Client!.PutObjectLegalHoldAsync(request, ct), ct);
        }

        /// <summary>
        /// Gets Object Lock retention information for an object.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Retention information or null if not set.</returns>
        public async Task<(string Mode, DateTime RetainUntilDate)?> GetObjectLockRetentionAsync(
            string key,
            CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            try
            {
                var request = new GetObjectRetentionRequest
                {
                    BucketName = _bucket,
                    Key = key
                };

                var response = await ExecuteWithRetryAsync(async () =>
                    await _s3Client!.GetObjectRetentionAsync(request, ct), ct);

                if (response.Retention != null && response.Retention.RetainUntilDate.HasValue)
                {
                    var mode = response.Retention.Mode == ObjectLockRetentionMode.Governance ? "GOVERNANCE" : "COMPLIANCE";
                    return (mode, response.Retention.RetainUntilDate.Value);
                }

                return null;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        /// <summary>
        /// Copies an object within B2.
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
        /// Configures bucket lifecycle rules for automatic expiration or transitions.
        /// </summary>
        /// <param name="rules">Lifecycle rules to configure.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task SetBucketLifecycleConfigurationAsync(
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
        /// Gets the bucket lifecycle configuration.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Lifecycle rules or null if not configured.</returns>
        public async Task<List<Amazon.S3.Model.LifecycleRule>?> GetBucketLifecycleConfigurationAsync(CancellationToken ct = default)
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
        /// Enables versioning on the bucket.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async Task EnableBucketVersioningAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            var request = new PutBucketVersioningRequest
            {
                BucketName = _bucket,
                VersioningConfig = new S3BucketVersioningConfig
                {
                    Status = VersionStatus.Enabled
                }
            };

            await ExecuteWithRetryAsync(async () =>
                await _s3Client!.PutBucketVersioningAsync(request, ct), ct);

            _enableVersioning = true;
        }

        /// <summary>
        /// Gets the current bucket versioning status.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Versioning status (Enabled, Suspended, or Off).</returns>
        public async Task<string> GetBucketVersioningStatusAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            var request = new GetBucketVersioningRequest
            {
                BucketName = _bucket
            };

            var response = await ExecuteWithRetryAsync(async () =>
                await _s3Client!.GetBucketVersioningAsync(request, ct), ct);

            return response.VersioningConfig.Status.ToString();
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
                       s3Ex.ErrorCode == "InternalError";
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
                ".mp4" => "video/mp4",
                ".avi" => "video/x-msvideo",
                ".mov" => "video/quicktime",
                ".wmv" => "video/x-ms-wmv",
                ".flv" => "video/x-flv",
                ".webm" => "video/webm",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".ogg" => "audio/ogg",
                ".flac" => "audio/flac",
                ".aac" => "audio/aac",
                ".doc" or ".docx" => "application/msword",
                ".xls" or ".xlsx" => "application/vnd.ms-excel",
                ".ppt" or ".pptx" => "application/vnd.ms-powerpoint",
                _ => "application/octet-stream"
            };
        }

        protected override int GetMaxKeyLength() => 1024; // B2 max object name length (via S3 API)

        #endregion
    }
}
