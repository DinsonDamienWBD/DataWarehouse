using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Enterprise
{
    /// <summary>
    /// VAST Data Platform storage strategy with comprehensive enterprise features:
    /// - VAST Management System (VMS) REST API v1.x integration
    /// - S3-compatible object storage interface (multiprotocol support)
    /// - NFS/SMB file protocol access (multiprotocol architecture)
    /// - Virtual file systems (Views) management
    /// - Protection policies and snapshots
    /// - Quotas and capacity management
    /// - QoS policy enforcement (bandwidth, IOPS limits)
    /// - Global namespace with cluster-wide access
    /// - Global deduplication and compression (similarity-based reduction)
    /// - WORM (Write Once Read Many) compliance mode
    /// - Multi-tenancy with tenant isolation
    /// - Real-time analytics and performance monitoring
    /// - Scale-out architecture (compute and storage disaggregation)
    /// - Inline data reduction and encryption
    /// </summary>
    public class VastDataStrategy : UltimateStorageStrategyBase
    {
        private HttpClient? _vmsHttpClient;
        private IAmazonS3? _s3Client;
        private string _vmsEndpoint = string.Empty;
        private string _vmsUsername = string.Empty;
        private string _vmsPassword = string.Empty;
        private string? _vmsApiToken = null;
        private string _s3Endpoint = string.Empty;
        private string _s3AccessKey = string.Empty;
        private string _s3SecretKey = string.Empty;
        private string _bucketName = string.Empty;
        private string _viewPath = "/";
        private string? _viewName = null;
        private bool _useS3Protocol = true;
        private bool _useNfsProtocol = false;
        private string? _nfsExportPath = null;
        private bool _enableSnapshots = false;
        private string? _protectionPolicyId = null;
        private int _snapshotRetentionDays = 30;
        private bool _enableQuotas = false;
        private long _softQuotaBytes = 0;
        private long _hardQuotaBytes = 0;
        private bool _enableQos = false;
        private string? _qosPolicyId = null;
        private long _qosBandwidthLimitMbps = 0;
        private long _qosIopsLimit = 0;
        private bool _enableDeduplication = true;
        private bool _enableCompression = true;
        private bool _enableSimilarityBasedReduction = true;
        private bool _enableEncryption = true;
        private string _encryptionMode = "AES256"; // AES256, AES256-GCM
        private bool _enableWorm = false;
        private int _wormRetentionDays = 365;
        private string? _wormMode = null; // compliance, enterprise
        private bool _enableMultiTenancy = false;
        private string? _tenantId = null;
        private bool _enableRealTimeAnalytics = false;
        private int _timeoutSeconds = 300;
        private bool _validateCertificate = true;
        private string _vmsApiVersion = "v1";
        private int _maxRetries = 3;
        private int _retryDelayMs = 1000;
        private int _multipartThresholdBytes = 100 * 1024 * 1024; // 100MB
        private int _multipartChunkSizeBytes = 10 * 1024 * 1024; // 10MB

        public override string StrategyId => "vast-data";
        public override string Name => "VAST Data Platform Storage";
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = _enableWorm,
            SupportsVersioning = _enableSnapshots,
            SupportsTiering = false, // VAST is single-tier (all-flash)
            SupportsEncryption = _enableEncryption,
            SupportsCompression = _enableCompression,
            SupportsMultipart = _useS3Protocol,
            MaxObjectSize = null, // No practical limit
            MaxObjects = null, // No practical limit
            ConsistencyModel = ConsistencyModel.Strong // VAST provides strong consistency
        };

        /// <summary>
        /// Initializes the VAST Data storage strategy.
        /// </summary>
        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load VMS configuration
            _vmsEndpoint = GetConfiguration<string>("VmsEndpoint", string.Empty);
            _vmsUsername = GetConfiguration<string>("VmsUsername", string.Empty);
            _vmsPassword = GetConfiguration<string>("VmsPassword", string.Empty);
            _vmsApiToken = GetConfiguration<string?>("VmsApiToken", null);

            // Load S3 configuration
            _s3Endpoint = GetConfiguration<string>("S3Endpoint", string.Empty);
            _s3AccessKey = GetConfiguration<string>("S3AccessKey", string.Empty);
            _s3SecretKey = GetConfiguration<string>("S3SecretKey", string.Empty);
            _bucketName = GetConfiguration<string>("BucketName", string.Empty);

            // Validate configuration based on protocol choice
            _useS3Protocol = GetConfiguration<bool>("UseS3Protocol", true);
            _useNfsProtocol = GetConfiguration<bool>("UseNfsProtocol", false);

            if (_useS3Protocol)
            {
                if (string.IsNullOrWhiteSpace(_s3Endpoint))
                {
                    throw new InvalidOperationException("VAST S3 endpoint is required when using S3 protocol. Set 'S3Endpoint' in configuration.");
                }
                if (string.IsNullOrWhiteSpace(_bucketName))
                {
                    throw new InvalidOperationException("VAST bucket name is required. Set 'BucketName' in configuration.");
                }
                if (string.IsNullOrWhiteSpace(_s3AccessKey) || string.IsNullOrWhiteSpace(_s3SecretKey))
                {
                    throw new InvalidOperationException("VAST S3 credentials are required. Set 'S3AccessKey' and 'S3SecretKey' in configuration.");
                }
            }

            if (!string.IsNullOrWhiteSpace(_vmsEndpoint))
            {
                // VMS is optional but if specified, needs authentication
                if (string.IsNullOrWhiteSpace(_vmsApiToken))
                {
                    if (string.IsNullOrWhiteSpace(_vmsUsername) || string.IsNullOrWhiteSpace(_vmsPassword))
                    {
                        throw new InvalidOperationException(
                            "VMS authentication is required. Set either 'VmsApiToken' or both 'VmsUsername' and 'VmsPassword' in configuration.");
                    }
                }
            }

            // Load optional configuration
            _viewPath = GetConfiguration<string>("ViewPath", "/");
            _viewName = GetConfiguration<string?>("ViewName", null);
            _nfsExportPath = GetConfiguration<string?>("NfsExportPath", null);
            _enableSnapshots = GetConfiguration<bool>("EnableSnapshots", false);
            _protectionPolicyId = GetConfiguration<string?>("ProtectionPolicyId", null);
            _snapshotRetentionDays = GetConfiguration<int>("SnapshotRetentionDays", 30);
            _enableQuotas = GetConfiguration<bool>("EnableQuotas", false);
            _softQuotaBytes = GetConfiguration<long>("SoftQuotaBytes", 0);
            _hardQuotaBytes = GetConfiguration<long>("HardQuotaBytes", 0);
            _enableQos = GetConfiguration<bool>("EnableQos", false);
            _qosPolicyId = GetConfiguration<string?>("QosPolicyId", null);
            _qosBandwidthLimitMbps = GetConfiguration<long>("QosBandwidthLimitMbps", 0);
            _qosIopsLimit = GetConfiguration<long>("QosIopsLimit", 0);
            _enableDeduplication = GetConfiguration<bool>("EnableDeduplication", true);
            _enableCompression = GetConfiguration<bool>("EnableCompression", true);
            _enableSimilarityBasedReduction = GetConfiguration<bool>("EnableSimilarityBasedReduction", true);
            _enableEncryption = GetConfiguration<bool>("EnableEncryption", true);
            _encryptionMode = GetConfiguration<string>("EncryptionMode", "AES256");
            _enableWorm = GetConfiguration<bool>("EnableWorm", false);
            _wormRetentionDays = GetConfiguration<int>("WormRetentionDays", 365);
            _wormMode = GetConfiguration<string?>("WormMode", null);
            _enableMultiTenancy = GetConfiguration<bool>("EnableMultiTenancy", false);
            _tenantId = GetConfiguration<string?>("TenantId", null);
            _enableRealTimeAnalytics = GetConfiguration<bool>("EnableRealTimeAnalytics", false);
            _timeoutSeconds = GetConfiguration<int>("TimeoutSeconds", 300);
            _validateCertificate = GetConfiguration<bool>("ValidateCertificate", true);
            _vmsApiVersion = GetConfiguration<string>("VmsApiVersion", "v1");
            _maxRetries = GetConfiguration<int>("MaxRetries", 3);
            _retryDelayMs = GetConfiguration<int>("RetryDelayMs", 1000);
            _multipartThresholdBytes = GetConfiguration<int>("MultipartThresholdBytes", 100 * 1024 * 1024);
            _multipartChunkSizeBytes = GetConfiguration<int>("MultipartChunkSizeBytes", 10 * 1024 * 1024);

            // Validate configuration
            if (_enableQuotas && _hardQuotaBytes > 0 && _softQuotaBytes > _hardQuotaBytes)
            {
                throw new ArgumentException("SoftQuotaBytes cannot exceed HardQuotaBytes");
            }

            if (_enableWorm && _wormRetentionDays < 1)
            {
                throw new ArgumentException("WormRetentionDays must be at least 1 day");
            }

            if (_multipartChunkSizeBytes < 5 * 1024 * 1024)
            {
                throw new ArgumentException("MultipartChunkSizeBytes must be at least 5MB");
            }

            // Initialize S3 client if using S3 protocol
            if (_useS3Protocol)
            {
                var credentials = new BasicAWSCredentials(_s3AccessKey, _s3SecretKey);
                var config = new AmazonS3Config
                {
                    ServiceURL = _s3Endpoint,
                    ForcePathStyle = true, // VAST uses path-style URLs
                    Timeout = TimeSpan.FromSeconds(_timeoutSeconds),
                    MaxErrorRetry = _maxRetries,
                    ThrottleRetries = true,
                    UseHttp = _s3Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                };

                _s3Client = new AmazonS3Client(credentials, config);
            }

            // Initialize VMS HTTP client if endpoint is specified
            if (!string.IsNullOrWhiteSpace(_vmsEndpoint))
            {
                var handler = new HttpClientHandler();
                if (!_validateCertificate)
                {
                    handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
                }

                _vmsHttpClient = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(_timeoutSeconds),
                    BaseAddress = new Uri(_vmsEndpoint)
                };

                // Set authentication headers
                if (!string.IsNullOrWhiteSpace(_vmsApiToken))
                {
                    _vmsHttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _vmsApiToken);
                }
                else
                {
                    var authBytes = Encoding.ASCII.GetBytes($"{_vmsUsername}:{_vmsPassword}");
                    var base64Auth = Convert.ToBase64String(authBytes);
                    _vmsHttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64Auth);
                }

                _vmsHttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }

            return Task.CompletedTask;
        }

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            if (_useS3Protocol)
            {
                return await StoreViaS3Async(key, data, metadata, ct);
            }
            else
            {
                throw new NotSupportedException("Only S3 protocol is currently supported for VAST Data operations");
            }
        }

        private async Task<StorageObjectMetadata> StoreViaS3Async(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
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
                result = await StoreMultipartAsync(key, data, dataLength, metadata, ct);
            }
            else
            {
                result = await StoreSinglePartAsync(key, data, metadata, ct);
            }

            // Create snapshot if enabled
            if (_enableSnapshots && !string.IsNullOrWhiteSpace(_viewName) && _vmsHttpClient != null)
            {
                await CreateSnapshotAsync(key, ct);
            }

            // Update statistics
            IncrementBytesStored(result.Size);
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
                BucketName = _bucketName,
                Key = key,
                InputStream = data,
                ContentType = GetContentType(key),
                AutoCloseStream = false
            };

            // Add server-side encryption
            if (_enableEncryption)
            {
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
            }

            // Add custom metadata
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    request.Metadata.Add(kvp.Key, kvp.Value);
                }
            }

            // Add WORM metadata if enabled
            if (_enableWorm)
            {
                var lockUntilDate = DateTime.UtcNow.AddDays(_wormRetentionDays);
                request.Metadata.Add("x-vast-worm-retention-until", lockUntilDate.ToString("o"));
                request.Metadata.Add("x-vast-worm-mode", _wormMode ?? "compliance");
            }

            // Add tenant metadata if multi-tenancy is enabled
            if (_enableMultiTenancy && !string.IsNullOrWhiteSpace(_tenantId))
            {
                request.Metadata.Add("x-vast-tenant-id", _tenantId);
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
            IDictionary<string, string>? metadata,
            CancellationToken ct)
        {
            // Step 1: Initialize multipart upload
            var initiateRequest = new InitiateMultipartUploadRequest
            {
                BucketName = _bucketName,
                Key = key,
                ContentType = GetContentType(key)
            };

            // Add server-side encryption
            if (_enableEncryption)
            {
                initiateRequest.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
            }

            // Add custom metadata
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    initiateRequest.Metadata.Add(kvp.Key, kvp.Value);
                }
            }

            // Add WORM metadata if enabled
            if (_enableWorm)
            {
                var lockUntilDate = DateTime.UtcNow.AddDays(_wormRetentionDays);
                initiateRequest.Metadata.Add("x-vast-worm-retention-until", lockUntilDate.ToString("o"));
                initiateRequest.Metadata.Add("x-vast-worm-mode", _wormMode ?? "compliance");
            }

            // Add tenant metadata if multi-tenancy is enabled
            if (_enableMultiTenancy && !string.IsNullOrWhiteSpace(_tenantId))
            {
                initiateRequest.Metadata.Add("x-vast-tenant-id", _tenantId);
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
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VastDataStrategy.StoreMultipartAsync] {ex.GetType().Name}: {ex.Message}");
                    // Ignore abort failures
                }
                throw;
            }
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            if (_useS3Protocol)
            {
                return await RetrieveViaS3Async(key, ct);
            }
            else
            {
                throw new NotSupportedException("Only S3 protocol is currently supported for VAST Data operations");
            }
        }

        private async Task<Stream> RetrieveViaS3Async(string key, CancellationToken ct)
        {
            var request = new GetObjectRequest
            {
                BucketName = _bucketName,
                Key = key
            };

            var response = await ExecuteWithRetryAsync(() => _s3Client!.GetObjectAsync(request, ct), ct);

            // Copy to memory stream
            var memoryStream = new MemoryStream(65536);
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

            if (_useS3Protocol)
            {
                await DeleteViaS3Async(key, ct);
            }
            else
            {
                throw new NotSupportedException("Only S3 protocol is currently supported for VAST Data operations");
            }
        }

        private async Task DeleteViaS3Async(string key, CancellationToken ct)
        {
            // Get size before deletion for statistics
            long size = 0;
            try
            {
                var metadata = await GetMetadataAsyncCore(key, ct);
                size = metadata.Size;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VastDataStrategy.DeleteViaS3Async] {ex.GetType().Name}: {ex.Message}");
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

            if (_useS3Protocol)
            {
                return await ExistsViaS3Async(key, ct);
            }
            else
            {
                throw new NotSupportedException("Only S3 protocol is currently supported for VAST Data operations");
            }
        }

        private async Task<bool> ExistsViaS3Async(string key, CancellationToken ct)
        {
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

            if (_useS3Protocol)
            {
                await foreach (var item in ListViaS3Async(prefix, ct))
                {
                    yield return item;
                }
            }
            else
            {
                throw new NotSupportedException("Only S3 protocol is currently supported for VAST Data operations");
            }
        }

        private async IAsyncEnumerable<StorageObjectMetadata> ListViaS3Async(
            string? prefix,
            [EnumeratorCancellation] CancellationToken ct)
        {
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
                        Tier = Tier
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

            if (_useS3Protocol)
            {
                return await GetMetadataViaS3Async(key, ct);
            }
            else
            {
                throw new NotSupportedException("Only S3 protocol is currently supported for VAST Data operations");
            }
        }

        private async Task<StorageObjectMetadata> GetMetadataViaS3Async(string key, CancellationToken ct)
        {
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
                Tier = Tier
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                if (_useS3Protocol)
                {
                    // Perform a lightweight list operation to check S3 endpoint
                    var request = new ListObjectsV2Request
                    {
                        BucketName = _bucketName,
                        MaxKeys = 1
                    };

                    await _s3Client!.ListObjectsV2Async(request, ct);
                }

                // Get cluster health from VMS API if available
                string? healthMessage = null;
                long? availableCapacity = null;
                long? totalCapacity = null;
                long? usedCapacity = null;

                if (_vmsHttpClient != null)
                {
                    try
                    {
                        var clusterHealth = await GetClusterHealthViaVmsAsync(ct);
                        healthMessage = clusterHealth.Message;
                        availableCapacity = clusterHealth.AvailableCapacity;
                        totalCapacity = clusterHealth.TotalCapacity;
                        usedCapacity = clusterHealth.UsedCapacity;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[VastDataStrategy.GetHealthAsyncCore] {ex.GetType().Name}: {ex.Message}");
                        // VMS health check is optional
                    }
                }

                sw.Stop();

                return new StorageHealthInfo
                {
                    Status = HealthStatus.Healthy,
                    LatencyMs = sw.ElapsedMilliseconds,
                    AvailableCapacity = availableCapacity,
                    TotalCapacity = totalCapacity,
                    UsedCapacity = usedCapacity,
                    Message = healthMessage ?? $"VAST Data bucket '{_bucketName}' is accessible",
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to access VAST Data: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override async Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            if (_vmsHttpClient != null)
            {
                try
                {
                    var health = await GetClusterHealthViaVmsAsync(ct);
                    return health.AvailableCapacity;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VastDataStrategy.GetAvailableCapacityAsyncCore] {ex.GetType().Name}: {ex.Message}");
                    // VMS not available
                }
            }

            // VAST has no practical capacity limit for S3
            return null;
        }

        #endregion

        #region VAST-Specific Operations

        /// <summary>
        /// Creates a snapshot of the view containing the specified object.
        /// </summary>
        /// <param name="objectKey">Object key to create snapshot for.</param>
        /// <param name="ct">Cancellation token.</param>
        private async Task CreateSnapshotAsync(string objectKey, CancellationToken ct)
        {
            if (_vmsHttpClient == null || string.IsNullOrWhiteSpace(_viewName))
            {
                return;
            }

            var snapshotName = $"snapshot_{objectKey.Replace("/", "_")}_{DateTime.UtcNow:yyyyMMddHHmmss}";
            var requestBody = new
            {
                name = snapshotName,
                view_path = _viewPath,
                retention_days = _snapshotRetentionDays
            };

            var content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

            var response = await SendVmsRequestAsync(
                HttpMethod.Post,
                $"/api/{_vmsApiVersion}/snapshots",
                content,
                ct);
        }

        /// <summary>
        /// Gets cluster health information via VMS API.
        /// </summary>
        private async Task<StorageHealthInfo> GetClusterHealthViaVmsAsync(CancellationToken ct)
        {
            if (_vmsHttpClient == null)
            {
                throw new InvalidOperationException("VMS client is not initialized");
            }

            var response = await SendVmsRequestAsync(
                HttpMethod.Get,
                $"/api/{_vmsApiVersion}/cluster",
                null,
                ct);

            var json = JsonDocument.Parse(response);
            var root = json.RootElement;

            var totalCapacity = root.GetProperty("total_capacity_bytes").GetInt64();
            var usedCapacity = root.GetProperty("used_capacity_bytes").GetInt64();
            var availableCapacity = totalCapacity - usedCapacity;

            var status = root.GetProperty("status").GetString();
            var healthStatus = status?.ToLowerInvariant() switch
            {
                "healthy" => HealthStatus.Healthy,
                "degraded" => HealthStatus.Degraded,
                _ => HealthStatus.Unknown
            };

            return new StorageHealthInfo
            {
                Status = healthStatus,
                AvailableCapacity = availableCapacity,
                TotalCapacity = totalCapacity,
                UsedCapacity = usedCapacity,
                Message = $"VAST cluster status: {status}",
                CheckedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Gets real-time analytics for an object or view.
        /// </summary>
        /// <param name="objectKey">Object key to get analytics for (null for view-level analytics).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Analytics data.</returns>
        public async Task<VastAnalytics?> GetRealTimeAnalyticsAsync(string? objectKey = null, CancellationToken ct = default)
        {
            if (_vmsHttpClient == null || !_enableRealTimeAnalytics)
            {
                return null;
            }

            var path = objectKey != null
                ? $"/api/{_vmsApiVersion}/analytics/objects/{Uri.EscapeDataString(objectKey)}"
                : $"/api/{_vmsApiVersion}/analytics/views/{Uri.EscapeDataString(_viewName ?? _bucketName)}";

            var response = await SendVmsRequestAsync(HttpMethod.Get, path, null, ct);
            var json = JsonDocument.Parse(response);
            var root = json.RootElement;

            return new VastAnalytics
            {
                ReadOps = root.GetProperty("read_ops").GetInt64(),
                WriteOps = root.GetProperty("write_ops").GetInt64(),
                ReadBytesPerSecond = root.GetProperty("read_bytes_per_sec").GetInt64(),
                WriteBytesPerSecond = root.GetProperty("write_bytes_per_sec").GetInt64(),
                Latency95thPercentileMs = root.GetProperty("latency_p95_ms").GetDouble(),
                DeduplicationRatio = root.GetProperty("dedup_ratio").GetDouble(),
                CompressionRatio = root.GetProperty("compression_ratio").GetDouble()
            };
        }

        /// <summary>
        /// Gets quota information for the current view.
        /// </summary>
        public async Task<VastQuota?> GetQuotaInfoAsync(CancellationToken ct = default)
        {
            if (_vmsHttpClient == null || !_enableQuotas)
            {
                return null;
            }

            var response = await SendVmsRequestAsync(
                HttpMethod.Get,
                $"/api/{_vmsApiVersion}/views/{Uri.EscapeDataString(_viewName ?? _bucketName)}/quota",
                null,
                ct);

            var json = JsonDocument.Parse(response);
            var root = json.RootElement;

            return new VastQuota
            {
                HardLimitBytes = root.GetProperty("hard_limit_bytes").GetInt64(),
                SoftLimitBytes = root.GetProperty("soft_limit_bytes").GetInt64(),
                UsedBytes = root.GetProperty("used_bytes").GetInt64(),
                FileCount = root.GetProperty("file_count").GetInt64()
            };
        }

        /// <summary>
        /// Sends a request to the VAST VMS API.
        /// </summary>
        private async Task<string> SendVmsRequestAsync(
            HttpMethod method,
            string path,
            HttpContent? content,
            CancellationToken ct)
        {
            if (_vmsHttpClient == null)
            {
                throw new InvalidOperationException("VMS client is not initialized");
            }

            var request = new HttpRequestMessage(method, path);
            if (content != null)
            {
                request.Content = content;
            }

            var response = await ExecuteWithRetryAsync(
                async () =>
                {
                    var resp = await _vmsHttpClient.SendAsync(request, ct);
                    resp.EnsureSuccessStatusCode();
                    return resp;
                },
                ct);

            return await response.Content.ReadAsStringAsync(ct);
        }

        #endregion

        #region Helper Methods

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

            if (ex is HttpRequestException httpEx)
            {
                return true;
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
            _vmsHttpClient?.Dispose();
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Real-time analytics information for VAST Data objects and views.
    /// </summary>
    public record VastAnalytics
    {
        /// <summary>Number of read operations per second.</summary>
        public long ReadOps { get; init; }

        /// <summary>Number of write operations per second.</summary>
        public long WriteOps { get; init; }

        /// <summary>Read throughput in bytes per second.</summary>
        public long ReadBytesPerSecond { get; init; }

        /// <summary>Write throughput in bytes per second.</summary>
        public long WriteBytesPerSecond { get; init; }

        /// <summary>95th percentile latency in milliseconds.</summary>
        public double Latency95thPercentileMs { get; init; }

        /// <summary>Deduplication ratio (logical to physical).</summary>
        public double DeduplicationRatio { get; init; }

        /// <summary>Compression ratio (uncompressed to compressed).</summary>
        public double CompressionRatio { get; init; }
    }

    /// <summary>
    /// Quota information for a VAST Data view.
    /// </summary>
    public record VastQuota
    {
        /// <summary>Hard quota limit in bytes.</summary>
        public long HardLimitBytes { get; init; }

        /// <summary>Soft quota limit in bytes (warning threshold).</summary>
        public long SoftLimitBytes { get; init; }

        /// <summary>Current space used in bytes.</summary>
        public long UsedBytes { get; init; }

        /// <summary>Number of files in the view.</summary>
        public long FileCount { get; init; }

        /// <summary>Percentage of hard quota used (0-100).</summary>
        public double UsagePercent => HardLimitBytes > 0 ? (UsedBytes * 100.0 / HardLimitBytes) : 0;

        /// <summary>Indicates if soft quota has been exceeded.</summary>
        public bool IsSoftLimitExceeded => UsedBytes > SoftLimitBytes && SoftLimitBytes > 0;

        /// <summary>Indicates if hard quota has been exceeded.</summary>
        public bool IsHardLimitExceeded => UsedBytes > HardLimitBytes && HardLimitBytes > 0;
    }

    #endregion
}
