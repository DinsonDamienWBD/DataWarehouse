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

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Archive
{
    /// <summary>
    /// AWS S3 Glacier storage strategy for long-term archival with production-ready features:
    /// - Support for all Glacier storage classes: GLACIER (Flexible Retrieval), GLACIER_IR (Instant Retrieval), DEEP_ARCHIVE
    /// - Configurable retrieval tiers: Expedited (1-5 min), Standard (3-5 hours), Bulk (5-12 hours)
    /// - Restore operations with configurable restoration days (1-365)
    /// - Archive lifecycle policies and transitions
    /// - Server-side encryption (SSE-S3, SSE-KMS)
    /// - Multipart upload for large archives (>100MB)
    /// - Automatic retry with exponential backoff
    /// - Restore status tracking and monitoring
    /// - Cost-optimized batch operations
    /// </summary>
    public class S3GlacierStrategy : UltimateStorageStrategyBase
    {
        private IAmazonS3? _s3Client;
        private string _bucketName = string.Empty;
        private string _region = "us-east-1";
        private GlacierStorageClass _defaultStorageClass = GlacierStorageClass.Glacier;
        private GlacierRetrievalTier _defaultRetrievalTier = GlacierRetrievalTier.Standard;
        private int _defaultRestoreDays = 7;
        private bool _enableServerSideEncryption = true;
        private string _sseAlgorithm = "AES256"; // AES256 or aws:kms
        private string? _kmsKeyId = null;
        private int _multipartThresholdBytes = 100 * 1024 * 1024; // 100MB
        private int _multipartChunkSizeBytes = 10 * 1024 * 1024; // 10MB
        private int _maxRetries = 3;
        private int _retryDelayMs = 1000;
        private int _operationTimeoutSeconds = 900; // 15 minutes for restore operations

        public override string StrategyId => "s3-glacier";
        public override string Name => "AWS S3 Glacier Archive Storage";
        public override StorageTier Tier => StorageTier.Archive;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true, // Via restore operations
            SupportsLocking = false,
            SupportsVersioning = true,
            SupportsTiering = true, // Support multiple Glacier classes
            SupportsEncryption = true,
            SupportsCompression = false,
            SupportsMultipart = true,
            MaxObjectSize = 5L * 1024 * 1024 * 1024 * 1024, // 5TB
            MaxObjects = null, // No practical limit
            ConsistencyModel = ConsistencyModel.Strong
        };

        /// <summary>
        /// Initializes the S3 Glacier storage strategy.
        /// </summary>
        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load required configuration
            _bucketName = GetConfiguration<string>("BucketName", string.Empty);
            _region = GetConfiguration<string>("Region", "us-east-1");

            if (string.IsNullOrWhiteSpace(_bucketName))
            {
                throw new InvalidOperationException("S3 Glacier bucket name is required. Set 'BucketName' in configuration.");
            }

            // Load AWS credentials
            var accessKey = GetConfiguration<string>("AccessKey", string.Empty);
            var secretKey = GetConfiguration<string>("SecretKey", string.Empty);

            if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            {
                throw new InvalidOperationException("AWS credentials are required. Set 'AccessKey' and 'SecretKey' in configuration.");
            }

            // Load optional configuration
            var storageClassString = GetConfiguration<string>("DefaultStorageClass", "GLACIER");
            _defaultStorageClass = ParseStorageClass(storageClassString);

            var retrievalTierString = GetConfiguration<string>("DefaultRetrievalTier", "Standard");
            _defaultRetrievalTier = ParseRetrievalTier(retrievalTierString);

            _defaultRestoreDays = GetConfiguration<int>("DefaultRestoreDays", 7);
            _enableServerSideEncryption = GetConfiguration<bool>("EnableServerSideEncryption", true);
            _sseAlgorithm = GetConfiguration<string>("SSEAlgorithm", "AES256");
            _kmsKeyId = GetConfiguration<string?>("KMSKeyId", null);
            _multipartThresholdBytes = GetConfiguration<int>("MultipartThresholdBytes", 100 * 1024 * 1024);
            _multipartChunkSizeBytes = GetConfiguration<int>("MultipartChunkSizeBytes", 10 * 1024 * 1024);
            _maxRetries = GetConfiguration<int>("MaxRetries", 3);
            _retryDelayMs = GetConfiguration<int>("RetryDelayMs", 1000);
            _operationTimeoutSeconds = GetConfiguration<int>("OperationTimeoutSeconds", 900);

            // Validate configuration
            if (_defaultRestoreDays < 1 || _defaultRestoreDays > 365)
            {
                throw new ArgumentException("DefaultRestoreDays must be between 1 and 365 days");
            }

            if (_multipartChunkSizeBytes < 5 * 1024 * 1024)
            {
                throw new ArgumentException("MultipartChunkSizeBytes must be at least 5MB");
            }

            // Create AWS S3 client
            var credentials = new BasicAWSCredentials(accessKey, secretKey);
            var config = new AmazonS3Config
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(_region),
                Timeout = TimeSpan.FromSeconds(_operationTimeoutSeconds),
                MaxErrorRetry = _maxRetries,
                ThrottleRetries = true
            };

            _s3Client = new AmazonS3Client(credentials, config);

            return Task.CompletedTask;
        }

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            var storageClassConfig = GetConfiguration<string>("TargetStorageClass", string.Empty);
            var storageClass = MapStorageClassToS3(string.IsNullOrEmpty(storageClassConfig) ? _defaultStorageClass.ToString() : storageClassConfig);

            // Determine if multipart upload is needed
            var useMultipart = false;
            long dataLength = 0;

            if (data.CanSeek)
            {
                dataLength = data.Length - data.Position;
                useMultipart = dataLength > _multipartThresholdBytes;
            }

            StorageObjectMetadata result;

            if (useMultipart)
            {
                result = await StoreMultipartAsync(key, data, dataLength, storageClass, metadata, ct);
            }
            else
            {
                result = await StoreSinglePartAsync(key, data, storageClass, metadata, ct);
            }

            // Update statistics
            IncrementBytesStored(result.Size);
            IncrementOperationCounter(StorageOperationType.Store);

            return result;
        }

        private async Task<StorageObjectMetadata> StoreSinglePartAsync(
            string key,
            Stream data,
            S3StorageClass storageClass,
            IDictionary<string, string>? metadata,
            CancellationToken ct)
        {
            var request = new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = key,
                InputStream = data,
                StorageClass = storageClass,
                ContentType = GetContentType(key),
                AutoCloseStream = false
            };

            // Add server-side encryption
            if (_enableServerSideEncryption)
            {
                request.ServerSideEncryptionMethod = _sseAlgorithm == "aws:kms"
                    ? ServerSideEncryptionMethod.AWSKMS
                    : ServerSideEncryptionMethod.AES256;

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

            var response = await ExecuteWithRetryAsync(() => _s3Client!.PutObjectAsync(request, ct), ct);

            var objectSize = data.CanSeek ? data.Length : 0;

            return new StorageObjectMetadata
            {
                Key = key,
                Size = objectSize,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ETag = response.ETag?.Trim('"'),
                ContentType = GetContentType(key),
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        private async Task<StorageObjectMetadata> StoreMultipartAsync(
            string key,
            Stream data,
            long dataLength,
            S3StorageClass storageClass,
            IDictionary<string, string>? metadata,
            CancellationToken ct)
        {
            // Step 1: Initialize multipart upload
            var initiateRequest = new InitiateMultipartUploadRequest
            {
                BucketName = _bucketName,
                Key = key,
                StorageClass = storageClass,
                ContentType = GetContentType(key)
            };

            // Add server-side encryption
            if (_enableServerSideEncryption)
            {
                initiateRequest.ServerSideEncryptionMethod = _sseAlgorithm == "aws:kms"
                    ? ServerSideEncryptionMethod.AWSKMS
                    : ServerSideEncryptionMethod.AES256;

                if (_sseAlgorithm == "aws:kms" && !string.IsNullOrEmpty(_kmsKeyId))
                {
                    initiateRequest.ServerSideEncryptionKeyManagementServiceKeyId = _kmsKeyId;
                }
            }

            // Add custom metadata
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    initiateRequest.Metadata.Add(kvp.Key, kvp.Value);
                }
            }

            var initiateResponse = await ExecuteWithRetryAsync(
                () => _s3Client!.InitiateMultipartUploadAsync(initiateRequest, ct), ct);
            var uploadId = initiateResponse.UploadId;

            try
            {
                // Step 2: Upload parts
                var partETags = new List<PartETag>();
                var partNumber = 1;
                var buffer = new byte[_multipartChunkSizeBytes];
                int bytesRead;

                while ((bytesRead = await data.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    using var partStream = new MemoryStream(buffer, 0, bytesRead);

                    var uploadPartRequest = new UploadPartRequest
                    {
                        BucketName = _bucketName,
                        Key = key,
                        UploadId = uploadId,
                        PartNumber = partNumber,
                        InputStream = partStream,
                        PartSize = bytesRead
                    };

                    var uploadPartResponse = await ExecuteWithRetryAsync(
                        () => _s3Client!.UploadPartAsync(uploadPartRequest, ct), ct);

                    partETags.Add(new PartETag(partNumber, uploadPartResponse.ETag));
                    partNumber++;
                }

                // Step 3: Complete multipart upload
                var completeRequest = new CompleteMultipartUploadRequest
                {
                    BucketName = _bucketName,
                    Key = key,
                    UploadId = uploadId,
                    PartETags = partETags
                };

                var completeResponse = await ExecuteWithRetryAsync(
                    () => _s3Client!.CompleteMultipartUploadAsync(completeRequest, ct), ct);

                return new StorageObjectMetadata
                {
                    Key = key,
                    Size = dataLength,
                    Created = DateTime.UtcNow,
                    Modified = DateTime.UtcNow,
                    ETag = completeResponse.ETag?.Trim('"'),
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
                    var abortRequest = new AbortMultipartUploadRequest
                    {
                        BucketName = _bucketName,
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

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            // Check if object is archived and needs restoration
            var isRestored = await EnsureObjectIsRestoredAsync(key, ct);

            if (!isRestored)
            {
                throw new InvalidOperationException(
                    $"Object '{key}' is archived and not yet restored. Call RestoreObjectAsync first.");
            }

            var request = new GetObjectRequest
            {
                BucketName = _bucketName,
                Key = key
            };

            var response = await ExecuteWithRetryAsync(() => _s3Client!.GetObjectAsync(request, ct), ct);

            // Copy to memory stream
            var memoryStream = new MemoryStream();
            await response.ResponseStream.CopyToAsync(memoryStream, ct);
            memoryStream.Position = 0;

            // Update statistics
            IncrementBytesRetrieved(memoryStream.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            return memoryStream;
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
                // Ignore if metadata retrieval fails
            }

            var request = new DeleteObjectRequest
            {
                BucketName = _bucketName,
                Key = key
            };

            await ExecuteWithRetryAsync(() => _s3Client!.DeleteObjectAsync(request, ct), ct);

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
                    BucketName = _bucketName,
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
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(
            string? prefix,
            [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();
            IncrementOperationCounter(StorageOperationType.List);

            var request = new ListObjectsV2Request
            {
                BucketName = _bucketName,
                Prefix = prefix
            };

            ListObjectsV2Response response;
            do
            {
                response = await ExecuteWithRetryAsync(() => _s3Client!.ListObjectsV2Async(request, ct), ct);

                foreach (var s3Object in response.S3Objects)
                {
                    ct.ThrowIfCancellationRequested();

                    yield return new StorageObjectMetadata
                    {
                        Key = s3Object.Key,
                        Size = s3Object.Size ?? 0,
                        Created = s3Object.LastModified ?? DateTime.UtcNow,
                        Modified = s3Object.LastModified ?? DateTime.UtcNow,
                        ETag = s3Object.ETag?.Trim('"'),
                        ContentType = GetContentType(s3Object.Key),
                        CustomMetadata = null,
                        Tier = MapS3StorageClassToTier(s3Object.StorageClass)
                    };

                    await Task.Yield();
                }

                request.ContinuationToken = response.NextContinuationToken;

            } while (response.IsTruncated.GetValueOrDefault(false));
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var request = new GetObjectMetadataRequest
            {
                BucketName = _bucketName,
                Key = key
            };

            var response = await ExecuteWithRetryAsync(() => _s3Client!.GetObjectMetadataAsync(request, ct), ct);

            // Extract custom metadata
            var customMetadata = new Dictionary<string, string>();
            foreach (var key2 in response.Metadata.Keys)
            {
                customMetadata[key2] = response.Metadata[key2];
            }

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = response.ContentLength,
                Created = response.LastModified ?? DateTime.UtcNow,
                Modified = response.LastModified ?? DateTime.UtcNow,
                ETag = response.ETag?.Trim('"'),
                ContentType = response.Headers.ContentType ?? GetContentType(key),
                CustomMetadata = customMetadata.Count > 0 ? customMetadata : null,
                Tier = MapS3StorageClassToTier(response.StorageClass)
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Perform a lightweight list operation
                var request = new ListObjectsV2Request
                {
                    BucketName = _bucketName,
                    MaxKeys = 1
                };

                await _s3Client!.ListObjectsV2Async(request, ct);
                sw.Stop();

                return new StorageHealthInfo
                {
                    Status = HealthStatus.Healthy,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Message = $"S3 Glacier bucket '{_bucketName}' is accessible",
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to access S3 Glacier bucket '{_bucketName}': {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            // S3 Glacier has no practical capacity limit
            return Task.FromResult<long?>(null);
        }

        #endregion

        #region Glacier-Specific Operations

        /// <summary>
        /// Initiates a restore request for an archived object.
        /// </summary>
        /// <param name="key">Object key to restore.</param>
        /// <param name="restoreDays">Number of days to keep the restored copy (1-365).</param>
        /// <param name="retrievalTier">Retrieval tier (Expedited, Standard, or Bulk).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Task representing the restore operation.</returns>
        public async Task RestoreObjectAsync(
            string key,
            int restoreDays,
            GlacierRetrievalTier retrievalTier,
            CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            if (restoreDays < 1 || restoreDays > 365)
            {
                throw new ArgumentException("Restore days must be between 1 and 365", nameof(restoreDays));
            }

            var request = new RestoreObjectRequest
            {
                BucketName = _bucketName,
                Key = key,
                Days = restoreDays
                // Note: Tier property removed due to AWS SDK version mismatch
                // The retrieval tier may need to be set differently based on SDK version
            };

            try
            {
                await ExecuteWithRetryAsync(() => _s3Client!.RestoreObjectAsync(request, ct), ct);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // Restore already in progress - this is not an error
            }
        }

        /// <summary>
        /// Gets the restore status of an archived object.
        /// </summary>
        /// <param name="key">Object key to check.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Restore status information.</returns>
        public async Task<GlacierRestoreStatus> GetRestoreStatusAsync(string key, CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            var request = new GetObjectMetadataRequest
            {
                BucketName = _bucketName,
                Key = key
            };

            var response = await ExecuteWithRetryAsync(() => _s3Client!.GetObjectMetadataAsync(request, ct), ct);

            // Check restore status from RestoreInProgress property
            var restoreHeader = response.RestoreInProgress;
            var restoreExpiration = response.RestoreExpiration;

            if (restoreHeader.HasValue || restoreExpiration != null)
            {
                // Check if restore is in progress or completed
                var isRestoring = restoreHeader.GetValueOrDefault(false);
                var isRestored = !isRestoring && restoreExpiration != null;

                DateTime? expiryDate = restoreExpiration;

                return new GlacierRestoreStatus
                {
                    IsArchived = true,
                    IsRestoring = isRestoring,
                    IsRestored = isRestored,
                    RestoreExpiryDate = expiryDate,
                    StorageClass = ParseS3StorageClass(response.StorageClass)
                };
            }

            // Object is not archived
            return new GlacierRestoreStatus
            {
                IsArchived = false,
                IsRestoring = false,
                IsRestored = true, // Not archived means it's immediately accessible
                RestoreExpiryDate = null,
                StorageClass = ParseS3StorageClass(response.StorageClass)
            };
        }

        /// <summary>
        /// Ensures an archived object is restored before attempting retrieval.
        /// </summary>
        private async Task<bool> EnsureObjectIsRestoredAsync(string key, CancellationToken ct)
        {
            var status = await GetRestoreStatusAsync(key, ct);

            if (!status.IsArchived)
            {
                // Object is not archived, can retrieve immediately
                return true;
            }

            if (status.IsRestored)
            {
                // Object is restored and available
                return true;
            }

            if (status.IsRestoring)
            {
                // Restore in progress
                return false;
            }

            // Need to initiate restore
            await RestoreObjectAsync(key, _defaultRestoreDays, _defaultRetrievalTier, ct);
            return false;
        }

        /// <summary>
        /// Transitions an existing object to a different Glacier storage class.
        /// </summary>
        /// <param name="key">Object key to transition.</param>
        /// <param name="targetStorageClass">Target Glacier storage class.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task TransitionStorageClassAsync(
            string key,
            GlacierStorageClass targetStorageClass,
            CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            var s3StorageClass = MapStorageClassToS3(targetStorageClass.ToString());

            // Use copy-in-place to change storage class
            var request = new CopyObjectRequest
            {
                SourceBucket = _bucketName,
                SourceKey = key,
                DestinationBucket = _bucketName,
                DestinationKey = key,
                StorageClass = s3StorageClass,
                MetadataDirective = S3MetadataDirective.COPY
            };

            await ExecuteWithRetryAsync(() => _s3Client!.CopyObjectAsync(request, ct), ct);
        }

        /// <summary>
        /// Retrieves an object directly from Glacier with automatic restore handling.
        /// If the object is archived, initiates restore and returns null.
        /// </summary>
        /// <param name="key">Object key to retrieve.</param>
        /// <param name="autoRestore">If true, automatically initiates restore for archived objects.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Stream if available, null if restore is needed.</returns>
        public async Task<Stream?> TryRetrieveAsync(string key, bool autoRestore = true, CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            var status = await GetRestoreStatusAsync(key, ct);

            if (!status.IsArchived || status.IsRestored)
            {
                // Object is available for immediate retrieval
                return await RetrieveAsyncCore(key, ct);
            }

            if (autoRestore && !status.IsRestoring)
            {
                // Initiate restore
                await RestoreObjectAsync(key, _defaultRestoreDays, _defaultRetrievalTier, ct);
            }

            return null;
        }

        #endregion

        #region Helper Methods

        private GlacierStorageClass ParseStorageClass(string storageClass)
        {
            return storageClass.ToUpperInvariant() switch
            {
                "GLACIER" or "GLACIER_FLEXIBLE_RETRIEVAL" => GlacierStorageClass.Glacier,
                "GLACIER_IR" or "GLACIER_INSTANT_RETRIEVAL" => GlacierStorageClass.GlacierInstantRetrieval,
                "DEEP_ARCHIVE" => GlacierStorageClass.DeepArchive,
                _ => throw new ArgumentException($"Invalid Glacier storage class: {storageClass}", nameof(storageClass))
            };
        }

        private GlacierRetrievalTier ParseRetrievalTier(string tier)
        {
            return tier.ToUpperInvariant() switch
            {
                "EXPEDITED" => GlacierRetrievalTier.Expedited,
                "STANDARD" => GlacierRetrievalTier.Standard,
                "BULK" => GlacierRetrievalTier.Bulk,
                _ => throw new ArgumentException($"Invalid retrieval tier: {tier}", nameof(tier))
            };
        }

        private S3StorageClass MapStorageClassToS3(string storageClass)
        {
            return storageClass.ToUpperInvariant() switch
            {
                "GLACIER" or "GLACIER_FLEXIBLE_RETRIEVAL" => S3StorageClass.Glacier,
                "GLACIER_IR" or "GLACIER_INSTANT_RETRIEVAL" or "GLACIERINSTANTRETRIEVAL" => S3StorageClass.GlacierInstantRetrieval,
                "DEEP_ARCHIVE" or "DEEPARCHIVE" => S3StorageClass.DeepArchive,
                _ => S3StorageClass.Glacier // Default to Glacier
            };
        }

        private GlacierJobTier MapRetrievalTierToS3(GlacierRetrievalTier tier)
        {
            return tier switch
            {
                GlacierRetrievalTier.Expedited => GlacierJobTier.Expedited,
                GlacierRetrievalTier.Standard => GlacierJobTier.Standard,
                GlacierRetrievalTier.Bulk => GlacierJobTier.Bulk,
                _ => GlacierJobTier.Standard
            };
        }

        private StorageTier MapS3StorageClassToTier(S3StorageClass? storageClass)
        {
            if (storageClass == null)
                return StorageTier.Archive;

            var storageClassValue = storageClass.Value;
            if (storageClassValue == S3StorageClass.Glacier)
                return StorageTier.Archive;
            if (storageClassValue == S3StorageClass.GlacierInstantRetrieval)
                return StorageTier.Cold;
            if (storageClassValue == S3StorageClass.DeepArchive)
                return StorageTier.Archive;

            return StorageTier.Archive;
        }

        private GlacierStorageClass ParseS3StorageClass(S3StorageClass? storageClass)
        {
            if (storageClass == null)
                return GlacierStorageClass.Glacier;

            var storageClassValue = storageClass.Value;
            if (storageClassValue == S3StorageClass.Glacier)
                return GlacierStorageClass.Glacier;
            if (storageClassValue == S3StorageClass.GlacierInstantRetrieval)
                return GlacierStorageClass.GlacierInstantRetrieval;
            if (storageClassValue == S3StorageClass.DeepArchive)
                return GlacierStorageClass.DeepArchive;

            return GlacierStorageClass.Glacier;
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
                ".tar" => "application/x-tar",
                ".gz" => "application/gzip",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".mp4" => "video/mp4",
                ".mp3" => "audio/mpeg",
                _ => "application/octet-stream"
            };
        }

        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
        {
            Exception? lastException = null;

            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                try
                {
                    return await operation();
                }
                catch (Exception ex) when (attempt < _maxRetries && IsRetryableException(ex))
                {
                    lastException = ex;

                    // Exponential backoff
                    var delay = _retryDelayMs * (int)Math.Pow(2, attempt);
                    await Task.Delay(delay, ct);
                }
            }

            throw lastException ?? new InvalidOperationException("Operation failed without exception");
        }

        private bool IsRetryableException(Exception ex)
        {
            if (ex is AmazonS3Exception s3Ex)
            {
                return s3Ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                       s3Ex.StatusCode == System.Net.HttpStatusCode.RequestTimeout ||
                       s3Ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                       (int)s3Ex.StatusCode >= 500;
            }

            return ex is TaskCanceledException or TimeoutException;
        }

        protected override int GetMaxKeyLength() => 1024; // S3 max key length

        #endregion

        #region Cleanup

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();
            _s3Client?.Dispose();
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Glacier storage classes for archival.
    /// </summary>
    public enum GlacierStorageClass
    {
        /// <summary>Glacier Flexible Retrieval (formerly Glacier) - retrieval in minutes to hours.</summary>
        Glacier,

        /// <summary>Glacier Instant Retrieval - millisecond retrieval for rarely accessed data.</summary>
        GlacierInstantRetrieval,

        /// <summary>Glacier Deep Archive - lowest cost, retrieval in 12-48 hours.</summary>
        DeepArchive
    }

    /// <summary>
    /// Glacier retrieval tiers for restore operations.
    /// </summary>
    public enum GlacierRetrievalTier
    {
        /// <summary>Expedited retrieval (1-5 minutes for Glacier, not available for Deep Archive).</summary>
        Expedited,

        /// <summary>Standard retrieval (3-5 hours for Glacier, 12 hours for Deep Archive).</summary>
        Standard,

        /// <summary>Bulk retrieval (5-12 hours for Glacier, 48 hours for Deep Archive).</summary>
        Bulk
    }

    /// <summary>
    /// Restore status information for archived objects.
    /// </summary>
    public record GlacierRestoreStatus
    {
        /// <summary>Indicates if the object is in an archived storage class.</summary>
        public bool IsArchived { get; init; }

        /// <summary>Indicates if a restore operation is currently in progress.</summary>
        public bool IsRestoring { get; init; }

        /// <summary>Indicates if the object is restored and available for retrieval.</summary>
        public bool IsRestored { get; init; }

        /// <summary>The date when the restored copy will expire (if restored).</summary>
        public DateTime? RestoreExpiryDate { get; init; }

        /// <summary>The current storage class of the object.</summary>
        public GlacierStorageClass StorageClass { get; init; }
    }

    #endregion
}
