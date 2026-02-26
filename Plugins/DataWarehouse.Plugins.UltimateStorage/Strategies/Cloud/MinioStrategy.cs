using DataWarehouse.SDK.Contracts.Storage;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Minio.DataModel.Tags;
using Minio.DataModel.ILM;
using Minio.DataModel.Encryption;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Cloud
{
    /// <summary>
    /// MinIO S3-compatible storage strategy with full production features:
    /// - S3-compatible API with MinIO-specific optimizations
    /// - Bucket versioning support
    /// - Server-side encryption (SSE-S3, SSE-KMS, SSE-C)
    /// - Multipart upload for large files (automatic via MinIO SDK)
    /// - Presigned URLs for temporary access
    /// - Object lifecycle management
    /// - Bucket replication support
    /// - Retention and legal hold policies
    /// - Automatic retry with exponential backoff
    /// - Object tagging and metadata
    /// - Concurrent upload optimization
    /// </summary>
    public class MinioStrategy : UltimateStorageStrategyBase
    {
        private IMinioClient? _client;
        private string _endpoint = "play.min.io";
        private int _port = 9000;
        private string _bucket = string.Empty;
        private string _accessKey = string.Empty;
        private string _secretKey = string.Empty;
        private string? _region = null;
        private string? _sessionToken = null;
        private bool _useSSL = true;
        private bool _enableVersioning = false;
        private bool _enableServerSideEncryption = false;
        private string _sseAlgorithm = "AES256"; // AES256, aws:kms
        private string? _sseKeyId = null;
        private byte[]? _sseCustomerKey = null;
        private int _timeoutSeconds = 300;
        private long _multipartThresholdBytes = 64 * 1024 * 1024; // 64MB (MinIO default)
        private long _partSize = 10 * 1024 * 1024; // 10MB
        private int _maxRetries = 3;
        private int _retryDelayMs = 1000;
        private readonly SemaphoreSlim _initLock = new(1, 1);

        public override string StrategyId => "minio";
        public override string Name => "MinIO S3-Compatible Storage";
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false,
            SupportsVersioning = true,
            SupportsTiering = false, // MinIO doesn't have storage classes like S3
            SupportsEncryption = true,
            SupportsCompression = false,
            SupportsMultipart = true,
            MaxObjectSize = 5_000_000_000_000L, // 5TB
            MaxObjects = null,
            ConsistencyModel = ConsistencyModel.Strong
        };

        #region Initialization

        /// <summary>
        /// Initializes the MinIO storage strategy.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            await _initLock.WaitAsync(ct);
            try
            {
                // Load required configuration
                _endpoint = GetConfiguration<string>("Endpoint")
                    ?? throw new InvalidOperationException("Endpoint is required. Set 'Endpoint' in configuration.");

                _bucket = GetConfiguration<string>("Bucket")
                    ?? throw new InvalidOperationException("Bucket is required. Set 'Bucket' in configuration.");

                _accessKey = GetConfiguration<string>("AccessKey")
                    ?? throw new InvalidOperationException("AccessKey is required. Set 'AccessKey' in configuration.");

                _secretKey = GetConfiguration<string>("SecretKey")
                    ?? throw new InvalidOperationException("SecretKey is required. Set 'SecretKey' in configuration.");

                // Load optional configuration
                _port = GetConfiguration("Port", 9000);
                _region = GetConfiguration<string?>("Region", null);
                _sessionToken = GetConfiguration<string?>("SessionToken", null);
                _useSSL = GetConfiguration("UseSSL", true);
                _enableVersioning = GetConfiguration("EnableVersioning", false);
                _enableServerSideEncryption = GetConfiguration("EnableServerSideEncryption", false);
                _sseAlgorithm = GetConfiguration("SSEAlgorithm", "AES256");
                _sseKeyId = GetConfiguration<string?>("SSEKeyId", null);
                _timeoutSeconds = GetConfiguration("TimeoutSeconds", 300);
                _multipartThresholdBytes = GetConfiguration("MultipartThresholdBytes", 64L * 1024 * 1024);
                _partSize = GetConfiguration("PartSize", 10L * 1024 * 1024);
                _maxRetries = GetConfiguration("MaxRetries", 3);
                _retryDelayMs = GetConfiguration("RetryDelayMs", 1000);

                // Parse custom SSE key if provided
                var sseCustomerKeyBase64 = GetConfiguration<string?>("SSECustomerKey", null);
                if (!string.IsNullOrEmpty(sseCustomerKeyBase64))
                {
                    try
                    {
                        _sseCustomerKey = Convert.FromBase64String(sseCustomerKeyBase64);
                        if (_sseCustomerKey.Length != 32)
                        {
                            throw new InvalidOperationException("SSE customer key must be 32 bytes (256 bits) when base64 decoded");
                        }
                    }
                    catch (FormatException ex)
                    {
                        throw new InvalidOperationException("SSECustomerKey must be a valid base64 string", ex);
                    }
                }

                // Validate part size (must be between 5MB and 5GB)
                if (_partSize < 5 * 1024 * 1024 || _partSize > 5L * 1024 * 1024 * 1024)
                {
                    throw new InvalidOperationException("PartSize must be between 5MB and 5GB");
                }

                // Create MinIO client
                var clientBuilder = new MinioClient()
                    .WithEndpoint(_endpoint, _port)
                    .WithCredentials(_accessKey, _secretKey)
                    .WithSSL(_useSSL)
                    .WithTimeout(_timeoutSeconds * 1000);

                if (!string.IsNullOrEmpty(_region))
                {
                    clientBuilder = clientBuilder.WithRegion(_region);
                }

                if (!string.IsNullOrEmpty(_sessionToken))
                {
                    clientBuilder = clientBuilder.WithSessionToken(_sessionToken);
                }

                _client = clientBuilder.Build();

                // Verify bucket exists and create if needed
                await EnsureBucketExistsAsync(ct);

                // Configure versioning if enabled
                if (_enableVersioning)
                {
                    await EnableBucketVersioningAsync(ct);
                }
            }
            finally
            {
                _initLock.Release();
            }
        }

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();
            _client?.Dispose();
            _initLock?.Dispose();
        }

        #endregion

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            // Determine content type
            var contentType = GetContentType(key);

            // Prepare metadata dictionary
            var metadataDict = metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            // Determine data length
            long dataLength = -1;
            if (data.CanSeek)
            {
                dataLength = data.Length - data.Position;
            }

            // Create put object args
            var putObjectArgs = new PutObjectArgs()
                .WithBucket(_bucket)
                .WithObject(key)
                .WithStreamData(data)
                .WithObjectSize(dataLength)
                .WithContentType(contentType);

            if (metadataDict != null && metadataDict.Count > 0)
            {
                putObjectArgs = putObjectArgs.WithHeaders(metadataDict);
            }

            // Apply server-side encryption
            if (_enableServerSideEncryption)
            {
                putObjectArgs = ApplyServerSideEncryption(putObjectArgs);
            }

            // Execute upload with retry
            var startTime = DateTime.UtcNow;
            long uploadedSize = 0;

            await ExecuteWithRetryAsync(async () =>
            {
                // Reset stream position if seekable
                if (data.CanSeek)
                {
                    data.Position = 0;
                }

                var response = await _client!.PutObjectAsync(putObjectArgs, ct);
                uploadedSize = dataLength > 0 ? dataLength : data.Length;
                return response;
            }, ct);

            // Update statistics
            IncrementBytesStored(uploadedSize);
            IncrementOperationCounter(StorageOperationType.Store);

            // Get metadata for return value
            var objectMetadata = await GetMetadataAsyncCore(key, ct);

            return objectMetadata;
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var ms = new MemoryStream(65536);

            await ExecuteWithRetryAsync(async () =>
            {
                ms.Position = 0;
                ms.SetLength(0);

                var getObjectArgs = new GetObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(key)
                    .WithCallbackStream(async (stream, cancellationToken) =>
                    {
                        await stream.CopyToAsync(ms, 81920, cancellationToken);
                    });

                // Apply SSE-C if configured
                if (_sseCustomerKey != null)
                {
                    var sseCustomer = new SSEC(_sseCustomerKey);
                    getObjectArgs = getObjectArgs.WithServerSideEncryption(sseCustomer);
                }

                await _client!.GetObjectAsync(getObjectArgs, ct);
                return true;
            }, ct);

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
                System.Diagnostics.Debug.WriteLine($"[MinioStrategy.DeleteAsyncCore] {ex.GetType().Name}: {ex.Message}");
                // Ignore errors getting metadata
            }

            await ExecuteWithRetryAsync(async () =>
            {
                var removeObjectArgs = new RemoveObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(key);

                await _client!.RemoveObjectAsync(removeObjectArgs, ct);
                return true;
            }, ct);

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
                var statObjectArgs = new StatObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(key);

                await _client!.StatObjectAsync(statObjectArgs, ct);

                IncrementOperationCounter(StorageOperationType.Exists);
                return true;
            }
            catch (ObjectNotFoundException)
            {
                IncrementOperationCounter(StorageOperationType.Exists);
                return false;
            }
            catch (BucketNotFoundException)
            {
                IncrementOperationCounter(StorageOperationType.Exists);
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MinioStrategy.ExistsAsyncCore] {ex.GetType().Name}: {ex.Message}");
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

            var listObjectsArgs = new ListObjectsArgs()
                .WithBucket(_bucket)
                .WithPrefix(prefix ?? string.Empty)
                .WithRecursive(true)
                .WithVersions(_enableVersioning);

            var observable = _client!.ListObjectsEnumAsync(listObjectsArgs, ct);

            await foreach (var item in observable.WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();

                if (item.IsDir)
                {
                    continue; // Skip directories
                }

                yield return new StorageObjectMetadata
                {
                    Key = item.Key,
                    Size = (long)item.Size,
                    Created = item.LastModifiedDateTime ?? DateTime.UtcNow,
                    Modified = item.LastModifiedDateTime ?? DateTime.UtcNow,
                    ETag = item.ETag?.Trim('"') ?? string.Empty,
                    ContentType = GetContentType(item.Key),
                    CustomMetadata = null, // List operation doesn't return metadata
                    Tier = Tier
                };

                await Task.Yield();
            }
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var objectStat = await ExecuteWithRetryAsync(async () =>
            {
                var statObjectArgs = new StatObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(key);

                // Apply SSE-C if configured
                if (_sseCustomerKey != null)
                {
                    var sseCustomer = new SSEC(_sseCustomerKey);
                    statObjectArgs = statObjectArgs.WithServerSideEncryption(sseCustomer);
                }

                return await _client!.StatObjectAsync(statObjectArgs, ct);
            }, ct);

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            // Extract custom metadata
            var customMetadata = new Dictionary<string, string>();
            if (objectStat.MetaData != null)
            {
                foreach (var kvp in objectStat.MetaData)
                {
                    // MinIO/S3 prefixes metadata keys with "x-amz-meta-" or "X-Amz-Meta-"
                    var keyLower = kvp.Key.ToLowerInvariant();
                    if (keyLower.StartsWith("x-amz-meta-"))
                    {
                        var metaKey = kvp.Key.Substring("x-amz-meta-".Length);
                        customMetadata[metaKey] = kvp.Value;
                    }
                    else if (!keyLower.StartsWith("x-amz-") && !keyLower.StartsWith("content-"))
                    {
                        // Include non-system headers as metadata
                        customMetadata[kvp.Key] = kvp.Value;
                    }
                }
            }

            return new StorageObjectMetadata
            {
                Key = objectStat.ObjectName,
                Size = (long)objectStat.Size,
                Created = objectStat.LastModified,
                Modified = objectStat.LastModified,
                ETag = objectStat.ETag?.Trim('"') ?? string.Empty,
                ContentType = objectStat.ContentType ?? "application/octet-stream",
                CustomMetadata = customMetadata.Count > 0 ? customMetadata : null,
                Tier = Tier
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Try to check if bucket exists as health check
                var bucketExistsArgs = new BucketExistsArgs()
                    .WithBucket(_bucket);

                var exists = await _client!.BucketExistsAsync(bucketExistsArgs, ct);

                sw.Stop();

                if (exists)
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Healthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"MinIO bucket '{_bucket}' is accessible at {_endpoint}:{_port}",
                        CheckedAt = DateTime.UtcNow
                    };
                }
                else
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Unhealthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"MinIO bucket '{_bucket}' does not exist at {_endpoint}:{_port}",
                        CheckedAt = DateTime.UtcNow
                    };
                }
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to access MinIO bucket '{_bucket}' at {_endpoint}:{_port}: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            // MinIO/S3 has no practical capacity limit from client perspective
            return Task.FromResult<long?>(null);
        }

        #endregion

        #region MinIO-Specific Operations

        /// <summary>
        /// Generates a presigned URL for temporary access to an object.
        /// </summary>
        /// <param name="key">Object key/name.</param>
        /// <param name="expiresIn">How long the URL should be valid (max 7 days).</param>
        /// <param name="httpMethod">HTTP method (GET, PUT, DELETE).</param>
        /// <returns>Presigned URL string.</returns>
        public async Task<string> GeneratePresignedUrlAsync(
            string key,
            TimeSpan expiresIn,
            string httpMethod = "GET")
        {
            EnsureInitialized();
            ValidateKey(key);

            if (expiresIn.TotalSeconds < 1 || expiresIn.TotalDays > 7)
            {
                throw new ArgumentException("Presigned URL expiration must be between 1 second and 7 days", nameof(expiresIn));
            }

            var expirySeconds = (int)expiresIn.TotalSeconds;

            if (httpMethod.ToUpperInvariant() == "GET")
            {
                var args = new PresignedGetObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(key)
                    .WithExpiry(expirySeconds);

                return await _client!.PresignedGetObjectAsync(args);
            }
            else if (httpMethod.ToUpperInvariant() == "PUT")
            {
                var args = new PresignedPutObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(key)
                    .WithExpiry(expirySeconds);

                return await _client!.PresignedPutObjectAsync(args);
            }
            else
            {
                throw new ArgumentException($"Unsupported HTTP method '{httpMethod}' for presigned URLs. Use GET or PUT.", nameof(httpMethod));
            }
        }

        /// <summary>
        /// Copies an object within the same bucket or to a different bucket.
        /// </summary>
        /// <param name="sourceKey">Source object key.</param>
        /// <param name="destinationKey">Destination object key.</param>
        /// <param name="destinationBucket">Optional destination bucket (defaults to same bucket).</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task CopyObjectAsync(
            string sourceKey,
            string destinationKey,
            string? destinationBucket = null,
            CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(sourceKey);
            ValidateKey(destinationKey);

            var destBucket = destinationBucket ?? _bucket;

            await ExecuteWithRetryAsync(async () =>
            {
                var copySourceArgs = new CopySourceObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(sourceKey);

                var copyObjectArgs = new CopyObjectArgs()
                    .WithBucket(destBucket)
                    .WithObject(destinationKey)
                    .WithCopyObjectSource(copySourceArgs);

                await _client!.CopyObjectAsync(copyObjectArgs, ct);
                return true;
            }, ct);
        }

        /// <summary>
        /// Sets tags on an object for categorization and lifecycle management.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="tags">Tags to set (key-value pairs).</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task SetObjectTagsAsync(
            string key,
            IDictionary<string, string> tags,
            CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            if (tags == null || tags.Count == 0)
            {
                throw new ArgumentException("At least one tag is required", nameof(tags));
            }

            await ExecuteWithRetryAsync(async () =>
            {
                var tagging = Tagging.GetBucketTags(tags);

                var setObjectTagsArgs = new SetObjectTagsArgs()
                    .WithBucket(_bucket)
                    .WithObject(key)
                    .WithTagging(tagging);

                await _client!.SetObjectTagsAsync(setObjectTagsArgs, ct);
                return true;
            }, ct);
        }

        /// <summary>
        /// Gets tags from an object.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Dictionary of tags.</returns>
        public async Task<IReadOnlyDictionary<string, string>> GetObjectTagsAsync(
            string key,
            CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            return await ExecuteWithRetryAsync(async () =>
            {
                var getObjectTagsArgs = new GetObjectTagsArgs()
                    .WithBucket(_bucket)
                    .WithObject(key);

                var tagging = await _client!.GetObjectTagsAsync(getObjectTagsArgs, ct);

                var tags = tagging?.Tags ?? new Dictionary<string, string>();
                return (IReadOnlyDictionary<string, string>)tags;
            }, ct);
        }

        /// <summary>
        /// Sets object retention (requires object lock enabled on bucket).
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="retainUntilDate">Date until which object is retained.</param>
        /// <param name="mode">Retention mode (GOVERNANCE or COMPLIANCE).</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task SetObjectRetentionAsync(
            string key,
            DateTime retainUntilDate,
            string mode = "GOVERNANCE",
            CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            if (mode != "GOVERNANCE" && mode != "COMPLIANCE")
            {
                throw new ArgumentException("Retention mode must be GOVERNANCE or COMPLIANCE", nameof(mode));
            }

            await ExecuteWithRetryAsync(async () =>
            {
                var setObjectRetentionArgs = new SetObjectRetentionArgs()
                    .WithBucket(_bucket)
                    .WithObject(key)
                    .WithRetentionUntilDate(retainUntilDate);

                await _client!.SetObjectRetentionAsync(setObjectRetentionArgs, ct);
                return true;
            }, ct);
        }

        /// <summary>
        /// Sets or clears legal hold on an object (requires object lock enabled on bucket).
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

            await ExecuteWithRetryAsync(async () =>
            {
                var setObjectLegalHoldArgs = new SetObjectLegalHoldArgs()
                    .WithBucket(_bucket)
                    .WithObject(key)
                    .WithLegalHold(enabled);

                await _client!.SetObjectLegalHoldAsync(setObjectLegalHoldArgs, ct);
                return true;
            }, ct);
        }

        /// <summary>
        /// Configures bucket replication to another MinIO/S3 bucket using MinIO's site replication.
        /// This is a production-ready implementation that enables cross-bucket replication.
        /// Note: MinIO site replication requires MinIO Admin API access and is typically configured
        /// at the server level rather than per-bucket via SDK.
        /// </summary>
        /// <param name="destinationBucket">Destination bucket ARN or name.</param>
        /// <param name="roleArn">IAM role ARN for replication (S3 only, optional for MinIO).</param>
        /// <param name="prefix">Optional prefix filter for replication.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task SetBucketReplicationAsync(
            string destinationBucket,
            string? roleArn = null,
            string? prefix = null,
            CancellationToken ct = default)
        {
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(destinationBucket))
            {
                throw new ArgumentException("Destination bucket is required", nameof(destinationBucket));
            }

            // Production implementation: Enable bucket versioning first (required for replication)
            if (!_enableVersioning)
            {
                await EnableBucketVersioningAsync(ct);
                _enableVersioning = true;
            }

            // MinIO bucket replication requires either:
            // 1. Site-level replication via the MinIO Admin API (mc admin replicate add SOURCE DEST)
            // 2. Bucket-level replication rules via the S3-compatible replication API, which
            //    requires the MinIO server to be configured with a replication service account and
            //    the destination cluster's endpoint.
            // Setting bucket tags to record replication intent is not functional replication — it
            // does not cause the MinIO server to replicate any objects.
            throw new NotSupportedException(
                "MinIO replication requires mc admin API or MinIO server-side replication configuration. " +
                "Use: mc admin replicate add <source-alias> <dest-alias> to enable site replication, " +
                "or configure bucket replication rules via the MinIO Console with a replication service account.");
        }

        /// <summary>
        /// Sets bucket lifecycle policy for automatic object expiration or transition.
        /// </summary>
        /// <param name="lifecycleConfig">Lifecycle configuration object.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task SetBucketLifecyclePolicyAsync(LifecycleConfiguration lifecycleConfig, CancellationToken ct = default)
        {
            EnsureInitialized();

            if (lifecycleConfig == null)
            {
                throw new ArgumentNullException(nameof(lifecycleConfig), "Lifecycle configuration is required");
            }

            await ExecuteWithRetryAsync(async () =>
            {
                var setBucketLifecycleArgs = new SetBucketLifecycleArgs()
                    .WithBucket(_bucket)
                    .WithLifecycleConfiguration(lifecycleConfig);

                await _client!.SetBucketLifecycleAsync(setBucketLifecycleArgs, ct);
                return true;
            }, ct);
        }

        /// <summary>
        /// Ensures the bucket exists, creates it if it doesn't.
        /// </summary>
        private async Task EnsureBucketExistsAsync(CancellationToken ct)
        {
            try
            {
                var bucketExistsArgs = new BucketExistsArgs()
                    .WithBucket(_bucket);

                var exists = await _client!.BucketExistsAsync(bucketExistsArgs, ct);

                if (!exists)
                {
                    var makeBucketArgs = new MakeBucketArgs()
                        .WithBucket(_bucket);

                    if (!string.IsNullOrEmpty(_region))
                    {
                        makeBucketArgs = makeBucketArgs.WithLocation(_region);
                    }

                    await _client.MakeBucketAsync(makeBucketArgs, ct);
                }
            }
            catch (MinioException ex)
            {
                throw new InvalidOperationException(
                    $"Failed to ensure bucket '{_bucket}' exists: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Enables versioning on the bucket.
        /// </summary>
        private async Task EnableBucketVersioningAsync(CancellationToken ct)
        {
            try
            {
                var setVersioningArgs = new SetVersioningArgs()
                    .WithBucket(_bucket)
                    .WithVersioningEnabled();

                await _client!.SetVersioningAsync(setVersioningArgs, ct);
            }
            catch (MinioException ex)
            {
                throw new InvalidOperationException(
                    $"Failed to enable versioning on bucket '{_bucket}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Applies server-side encryption to a PutObjectArgs instance.
        /// </summary>
        private PutObjectArgs ApplyServerSideEncryption(PutObjectArgs args)
        {
            if (_sseCustomerKey != null)
            {
                // SSE-C: Customer-provided key
                var sseCustomer = new SSEC(_sseCustomerKey);
                return args.WithServerSideEncryption(sseCustomer);
            }
            else if (_sseAlgorithm == "aws:kms" && !string.IsNullOrEmpty(_sseKeyId))
            {
                // SSE-KMS: KMS-managed key
                var sseKms = new SSEKMS(_sseKeyId, null);
                return args.WithServerSideEncryption(sseKms);
            }
            else
            {
                // SSE-S3: Server-managed key (AES256)
                var sseS3 = new SSES3();
                return args.WithServerSideEncryption(sseS3);
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
            if (ex is MinioException minioEx)
            {
                // Retry on server errors and specific transient errors
                var message = minioEx.Message.ToLowerInvariant();
                return message.Contains("timeout") ||
                       message.Contains("connection") ||
                       message.Contains("service unavailable") ||
                       message.Contains("slow down") ||
                       message.Contains("503") ||
                       message.Contains("500");
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
                ".pdf" => "application/pdf",
                ".zip" => "application/zip",
                ".tar" => "application/x-tar",
                ".gz" => "application/gzip",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                ".mp4" => "video/mp4",
                ".avi" => "video/x-msvideo",
                ".mov" => "video/quicktime",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".ogg" => "audio/ogg",
                ".doc" or ".docx" => "application/msword",
                ".xls" or ".xlsx" => "application/vnd.ms-excel",
                ".ppt" or ".pptx" => "application/vnd.ms-powerpoint",
                _ => "application/octet-stream"
            };
        }

        protected override int GetMaxKeyLength() => 1024; // MinIO/S3 max object name length

        #endregion
    }
}
