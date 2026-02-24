using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Enterprise
{
    /// <summary>
    /// Pure Storage FlashBlade storage strategy with comprehensive enterprise features:
    /// - FlashBlade REST API 2.x integration
    /// - File systems and object store accounts management
    /// - S3-compatible object storage
    /// - NFS exports and SMB shares
    /// - Snapshots and snapshot policies
    /// - File replication and object replication
    /// - Data protection with SafeMode (immutability)
    /// - Bucket management and lifecycle rules
    /// - Array capacity and performance metrics
    /// - Directory quotas
    /// - Object lock (WORM compliance)
    /// - Multi-protocol support (S3, NFS, SMB)
    /// - High-performance parallel I/O
    /// </summary>
    public class PureStorageStrategy : UltimateStorageStrategyBase
    {
        private HttpClient? _httpClient;
        private string _endpoint = string.Empty;
        private string _apiToken = string.Empty;
        private string? _fileSystem = null;
        private string? _bucket = null;
        private bool _useS3Protocol = false;
        private string _basePath = "/";
        private bool _enableSnapshots = false;
        private string? _snapshotPolicy = null;
        private bool _enableReplication = false;
        private string? _replicationTarget = null;
        private bool _enableSafeMode = false;
        private int? _safeModeRetentionDays = null;
        private bool _enableObjectLock = false;
        private string? _objectLockMode = null; // governance, compliance
        private int? _objectLockRetentionDays = null;
        private bool _enableLifecycleRules = false;
        private int? _lifecycleTransitionDays = null;
        private bool _enableQuotas = false;
        private long? _hardQuotaBytes = null;
        private int _timeoutSeconds = 300;
        private bool _validateCertificate = true;
        private string _apiVersion = "2.4";
        private int _maxRetries = 3;
        private int _retryDelayMs = 1000;
        private bool _enableCompression = true;
        private bool _enableDeduplication = true;
        private string? _nfsExportRules = null;
        private string? _smbShareRules = null;

        public override string StrategyId => "pure-storage-flashblade";
        public override string Name => "Pure Storage FlashBlade";
        public override StorageTier Tier => StorageTier.Hot;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = _enableObjectLock,
            SupportsVersioning = _enableSnapshots,
            SupportsTiering = _enableLifecycleRules,
            SupportsEncryption = true, // FlashBlade supports encryption at rest
            SupportsCompression = _enableCompression,
            SupportsMultipart = _useS3Protocol, // S3 protocol supports multipart
            MaxObjectSize = null, // No practical limit
            MaxObjects = null, // Unlimited objects
            ConsistencyModel = ConsistencyModel.Strong // FlashBlade provides strong consistency
        };

        /// <summary>
        /// Initializes the Pure Storage FlashBlade storage strategy.
        /// </summary>
        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load required configuration
            _endpoint = GetConfiguration<string>("Endpoint", string.Empty);
            _apiToken = GetConfiguration<string>("ApiToken", string.Empty);

            // Validate required configuration
            if (string.IsNullOrWhiteSpace(_endpoint))
            {
                throw new InvalidOperationException("FlashBlade endpoint is required. Set 'Endpoint' in configuration.");
            }
            if (string.IsNullOrWhiteSpace(_apiToken))
            {
                throw new InvalidOperationException("FlashBlade API token is required. Set 'ApiToken' in configuration.");
            }

            // Load storage target configuration
            _fileSystem = GetConfiguration<string?>("FileSystem", null);
            _bucket = GetConfiguration<string?>("Bucket", null);
            _useS3Protocol = GetConfiguration<bool>("UseS3Protocol", false);

            // Validate storage target
            if (_useS3Protocol)
            {
                if (string.IsNullOrWhiteSpace(_bucket))
                {
                    throw new InvalidOperationException("Bucket name is required when UseS3Protocol is true. Set 'Bucket' in configuration.");
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_fileSystem))
                {
                    throw new InvalidOperationException("FileSystem name is required when UseS3Protocol is false. Set 'FileSystem' in configuration.");
                }
            }

            // Load optional configuration
            _basePath = GetConfiguration<string>("BasePath", "/");
            _enableSnapshots = GetConfiguration<bool>("EnableSnapshots", false);
            _snapshotPolicy = GetConfiguration<string?>("SnapshotPolicy", null);
            _enableReplication = GetConfiguration<bool>("EnableReplication", false);
            _replicationTarget = GetConfiguration<string?>("ReplicationTarget", null);
            _enableSafeMode = GetConfiguration<bool>("EnableSafeMode", false);
            _safeModeRetentionDays = GetConfiguration<int?>("SafeModeRetentionDays", null);
            _enableObjectLock = GetConfiguration<bool>("EnableObjectLock", false);
            _objectLockMode = GetConfiguration<string?>("ObjectLockMode", null);
            _objectLockRetentionDays = GetConfiguration<int?>("ObjectLockRetentionDays", null);
            _enableLifecycleRules = GetConfiguration<bool>("EnableLifecycleRules", false);
            _lifecycleTransitionDays = GetConfiguration<int?>("LifecycleTransitionDays", null);
            _enableQuotas = GetConfiguration<bool>("EnableQuotas", false);
            _hardQuotaBytes = GetConfiguration<long?>("HardQuotaBytes", null);
            _timeoutSeconds = GetConfiguration<int>("TimeoutSeconds", 300);
            _validateCertificate = GetConfiguration<bool>("ValidateCertificate", true);
            _apiVersion = GetConfiguration<string>("ApiVersion", "2.4");
            _maxRetries = GetConfiguration<int>("MaxRetries", 3);
            _retryDelayMs = GetConfiguration<int>("RetryDelayMs", 1000);
            _enableCompression = GetConfiguration<bool>("EnableCompression", true);
            _enableDeduplication = GetConfiguration<bool>("EnableDeduplication", true);
            _nfsExportRules = GetConfiguration<string?>("NfsExportRules", null);
            _smbShareRules = GetConfiguration<string?>("SmbShareRules", null);

            // Ensure base path starts with /
            if (!_basePath.StartsWith("/"))
            {
                _basePath = "/" + _basePath;
            }

            // Remove trailing slash from base path
            _basePath = _basePath.TrimEnd('/');

            // Create HTTP client with custom handler for certificate validation
            var handler = new HttpClientHandler();
            if (!_validateCertificate)
            {
                handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
            }

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(_timeoutSeconds),
                BaseAddress = new Uri(_endpoint)
            };

            // Set API version header and authentication
            _httpClient.DefaultRequestHeaders.Add("api-token", _apiToken);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            return Task.CompletedTask;
        }

        #region Core Storage Operations

        /// <summary>
        /// Stores data to Pure Storage FlashBlade.
        /// </summary>
        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            ValidateKey(key);
            ValidateStream(data);

            if (_useS3Protocol)
            {
                return await StoreViaS3ProtocolAsync(key, data, metadata, ct);
            }
            else
            {
                return await StoreViaRestApiAsync(key, data, metadata, ct);
            }
        }

        private async Task<StorageObjectMetadata> StoreViaRestApiAsync(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            var fullPath = GetFullPath(key);

            // Read data into memory
            using var ms = new MemoryStream(65536);
            await data.CopyToAsync(ms, 81920, ct);
            var content = ms.ToArray();

            // Upload file using FlashBlade REST API
            // API: POST /api/{version}/files
            var payload = new JsonObject
            {
                ["name"] = fullPath,
                ["file_system"] = new JsonObject
                {
                    ["name"] = _fileSystem
                }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/{_apiVersion}/files")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };

            var response = await SendWithRetryAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            // Upload file content
            // API: PUT /api/{version}/files/{name}/data
            var uploadRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/{_apiVersion}/files/{Uri.EscapeDataString(fullPath)}/data");
            uploadRequest.Content = new ByteArrayContent(content);
            uploadRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            // Add custom metadata as headers
            if (metadata != null)
            {
                var metadataJson = JsonSerializer.Serialize(metadata);
                uploadRequest.Headers.Add("X-Pure-Metadata", Convert.ToBase64String(Encoding.UTF8.GetBytes(metadataJson)));
            }

            var uploadResponse = await SendWithRetryAsync(uploadRequest, ct);

            // Create snapshot if enabled
            if (_enableSnapshots)
            {
                await CreateSnapshotAsync(_fileSystem!, $"snapshot_{key}_{DateTime.UtcNow:yyyyMMddHHmmss}", ct);
            }

            // Trigger replication if enabled
            if (_enableReplication && !string.IsNullOrWhiteSpace(_replicationTarget))
            {
                await TriggerReplicationAsync(_fileSystem!, ct);
            }

            // Apply SafeMode protection if enabled
            if (_enableSafeMode && _safeModeRetentionDays.HasValue)
            {
                await ApplySafeModeAsync(fullPath, _safeModeRetentionDays.Value, ct);
            }

            // Update statistics
            IncrementBytesStored(content.Length);
            IncrementOperationCounter(StorageOperationType.Store);

            // Calculate ETag
            var etag = CalculateETag(content);

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

        private async Task<StorageObjectMetadata> StoreViaS3ProtocolAsync(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            // FlashBlade S3 API follows standard S3 protocol
            // API: PUT /s3/{bucket}/{key}
            var fullPath = $"/{_bucket}/{key}";

            using var ms = new MemoryStream(65536);
            await data.CopyToAsync(ms, 81920, ct);
            var content = ms.ToArray();

            var request = new HttpRequestMessage(HttpMethod.Put, fullPath);
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(GetContentType(key));

            // Add S3 metadata headers
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    request.Headers.TryAddWithoutValidation($"x-amz-meta-{kvp.Key}", kvp.Value);
                }
            }

            // Add object lock headers if enabled
            if (_enableObjectLock && _objectLockRetentionDays.HasValue)
            {
                var retainUntilDate = DateTime.UtcNow.AddDays(_objectLockRetentionDays.Value);
                request.Headers.TryAddWithoutValidation("x-amz-object-lock-mode", _objectLockMode ?? "GOVERNANCE");
                request.Headers.TryAddWithoutValidation("x-amz-object-lock-retain-until-date", retainUntilDate.ToString("o"));
            }

            var response = await SendWithRetryAsync(request, ct);

            // Update statistics
            IncrementBytesStored(content.Length);
            IncrementOperationCounter(StorageOperationType.Store);

            var etag = CalculateETag(content);

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

        /// <summary>
        /// Retrieves data from Pure Storage FlashBlade.
        /// </summary>
        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            if (_useS3Protocol)
            {
                return await RetrieveViaS3ProtocolAsync(key, ct);
            }
            else
            {
                return await RetrieveViaRestApiAsync(key, ct);
            }
        }

        private async Task<Stream> RetrieveViaRestApiAsync(string key, CancellationToken ct)
        {
            var fullPath = GetFullPath(key);

            // Download file using FlashBlade REST API
            // API: GET /api/{version}/files/{name}/data
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/{_apiVersion}/files/{Uri.EscapeDataString(fullPath)}/data");
            var response = await SendWithRetryAsync(request, ct);

            var ms = new MemoryStream(65536);
            await response.Content.CopyToAsync(ms, ct);
            ms.Position = 0;

            // Update statistics
            IncrementBytesRetrieved(ms.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            return ms;
        }

        private async Task<Stream> RetrieveViaS3ProtocolAsync(string key, CancellationToken ct)
        {
            var fullPath = $"/{_bucket}/{key}";

            var request = new HttpRequestMessage(HttpMethod.Get, fullPath);
            var response = await SendWithRetryAsync(request, ct);

            var ms = new MemoryStream(65536);
            await response.Content.CopyToAsync(ms, ct);
            ms.Position = 0;

            // Update statistics
            IncrementBytesRetrieved(ms.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            return ms;
        }

        /// <summary>
        /// Deletes an object from Pure Storage FlashBlade.
        /// </summary>
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
                System.Diagnostics.Debug.WriteLine($"[PureStorageStrategy.DeleteAsyncCore] {ex.GetType().Name}: {ex.Message}");
                // Ignore if metadata retrieval fails
            }

            if (_useS3Protocol)
            {
                await DeleteViaS3ProtocolAsync(key, ct);
            }
            else
            {
                await DeleteViaRestApiAsync(key, ct);
            }

            // Update statistics
            if (size > 0)
            {
                IncrementBytesDeleted(size);
            }
            IncrementOperationCounter(StorageOperationType.Delete);
        }

        private async Task DeleteViaRestApiAsync(string key, CancellationToken ct)
        {
            var fullPath = GetFullPath(key);

            // Delete file using FlashBlade REST API
            // API: DELETE /api/{version}/files/{name}
            var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/{_apiVersion}/files/{Uri.EscapeDataString(fullPath)}");
            await SendWithRetryAsync(request, ct);

            // Create snapshot after deletion if enabled
            if (_enableSnapshots)
            {
                await CreateSnapshotAsync(_fileSystem!, $"snapshot_delete_{key}_{DateTime.UtcNow:yyyyMMddHHmmss}", ct);
            }
        }

        private async Task DeleteViaS3ProtocolAsync(string key, CancellationToken ct)
        {
            var fullPath = $"/{_bucket}/{key}";

            var request = new HttpRequestMessage(HttpMethod.Delete, fullPath);
            await SendWithRetryAsync(request, ct);
        }

        /// <summary>
        /// Checks if an object exists in Pure Storage FlashBlade.
        /// </summary>
        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            try
            {
                if (_useS3Protocol)
                {
                    return await ExistsViaS3ProtocolAsync(key, ct);
                }
                else
                {
                    return await ExistsViaRestApiAsync(key, ct);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PureStorageStrategy.ExistsAsyncCore] {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> ExistsViaRestApiAsync(string key, CancellationToken ct)
        {
            var fullPath = GetFullPath(key);

            // Check file existence using FlashBlade REST API
            // API: GET /api/{version}/files?names={name}
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/{_apiVersion}/files?names={Uri.EscapeDataString(fullPath)}");
            var response = await _httpClient!.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                var jsonDoc = JsonDocument.Parse(json);

                if (jsonDoc.RootElement.TryGetProperty("items", out var items))
                {
                    IncrementOperationCounter(StorageOperationType.Exists);
                    return items.GetArrayLength() > 0;
                }
            }

            IncrementOperationCounter(StorageOperationType.Exists);
            return false;
        }

        private async Task<bool> ExistsViaS3ProtocolAsync(string key, CancellationToken ct)
        {
            var fullPath = $"/{_bucket}/{key}";

            var request = new HttpRequestMessage(HttpMethod.Head, fullPath);
            var response = await _httpClient!.SendAsync(request, ct);

            IncrementOperationCounter(StorageOperationType.Exists);

            return response.IsSuccessStatusCode;
        }

        /// <summary>
        /// Lists objects in Pure Storage FlashBlade with an optional prefix filter.
        /// </summary>
        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.List);

            if (_useS3Protocol)
            {
                await foreach (var item in ListViaS3ProtocolAsync(prefix, ct))
                {
                    yield return item;
                }
            }
            else
            {
                await foreach (var item in ListViaRestApiAsync(prefix, ct))
                {
                    yield return item;
                }
            }
        }

        private async IAsyncEnumerable<StorageObjectMetadata> ListViaRestApiAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            var searchPath = string.IsNullOrWhiteSpace(prefix) ? _basePath : GetFullPath(prefix);

            // List files using FlashBlade REST API
            // API: GET /api/{version}/files?file_system.name={fs}&path={path}*
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/{_apiVersion}/files?file_system.name={Uri.EscapeDataString(_fileSystem!)}&filter=path='{Uri.EscapeDataString(searchPath)}*'");
            var response = await SendWithRetryAsync(request, ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            var jsonDoc = JsonDocument.Parse(json);

            if (jsonDoc.RootElement.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();

                    var name = item.GetProperty("name").GetString() ?? string.Empty;
                    var size = item.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0L;
                    var created = item.TryGetProperty("created", out var createdElement)
                        ? DateTime.Parse(createdElement.GetString() ?? DateTime.UtcNow.ToString())
                        : DateTime.UtcNow;

                    var relativePath = name.StartsWith(_basePath) ? name.Substring(_basePath.Length).TrimStart('/') : name;

                    yield return new StorageObjectMetadata
                    {
                        Key = relativePath,
                        Size = size,
                        Created = created,
                        Modified = created,
                        ETag = CalculateETag(name),
                        ContentType = GetContentType(name),
                        CustomMetadata = null,
                        Tier = Tier
                    };

                    await Task.Yield();
                }
            }
        }

        private async IAsyncEnumerable<StorageObjectMetadata> ListViaS3ProtocolAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            var queryPrefix = string.IsNullOrWhiteSpace(prefix) ? "" : $"?prefix={Uri.EscapeDataString(prefix)}";
            var request = new HttpRequestMessage(HttpMethod.Get, $"/{_bucket}/{queryPrefix}");
            var response = await SendWithRetryAsync(request, ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            var jsonDoc = JsonDocument.Parse(json);

            if (jsonDoc.RootElement.TryGetProperty("Contents", out var contents))
            {
                foreach (var item in contents.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();

                    var key = item.GetProperty("Key").GetString() ?? string.Empty;
                    var size = item.GetProperty("Size").GetInt64();
                    var lastModified = DateTime.Parse(item.GetProperty("LastModified").GetString() ?? DateTime.UtcNow.ToString());
                    var etag = item.TryGetProperty("ETag", out var etagElement) ? etagElement.GetString()?.Trim('"') ?? string.Empty : string.Empty;

                    yield return new StorageObjectMetadata
                    {
                        Key = key,
                        Size = size,
                        Created = lastModified,
                        Modified = lastModified,
                        ETag = etag,
                        ContentType = GetContentType(key),
                        CustomMetadata = null,
                        Tier = Tier
                    };

                    await Task.Yield();
                }
            }
        }

        /// <summary>
        /// Gets metadata for a specific object without retrieving its data.
        /// </summary>
        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            if (_useS3Protocol)
            {
                return await GetMetadataViaS3ProtocolAsync(key, ct);
            }
            else
            {
                return await GetMetadataViaRestApiAsync(key, ct);
            }
        }

        private async Task<StorageObjectMetadata> GetMetadataViaRestApiAsync(string key, CancellationToken ct)
        {
            var fullPath = GetFullPath(key);

            // Get file metadata using FlashBlade REST API
            // API: GET /api/{version}/files?names={name}
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/{_apiVersion}/files?names={Uri.EscapeDataString(fullPath)}");
            var response = await SendWithRetryAsync(request, ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            var jsonDoc = JsonDocument.Parse(json);

            if (jsonDoc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
            {
                var item = items[0];
                var size = item.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0L;
                var created = item.TryGetProperty("created", out var createdElement)
                    ? DateTime.Parse(createdElement.GetString() ?? DateTime.UtcNow.ToString())
                    : DateTime.UtcNow;

                // Extract custom metadata if present
                Dictionary<string, string>? customMetadata = null;
                if (response.Headers.TryGetValues("X-Pure-Metadata", out var metadataValues))
                {
                    var metadataBase64 = metadataValues.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(metadataBase64))
                    {
                        var metadataJson = Encoding.UTF8.GetString(Convert.FromBase64String(metadataBase64));
                        customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
                    }
                }

                IncrementOperationCounter(StorageOperationType.GetMetadata);

                return new StorageObjectMetadata
                {
                    Key = key,
                    Size = size,
                    Created = created,
                    Modified = created,
                    ETag = CalculateETag(key),
                    ContentType = GetContentType(key),
                    CustomMetadata = customMetadata,
                    Tier = Tier
                };
            }

            throw new FileNotFoundException($"Object '{key}' not found in FlashBlade");
        }

        private async Task<StorageObjectMetadata> GetMetadataViaS3ProtocolAsync(string key, CancellationToken ct)
        {
            var fullPath = $"/{_bucket}/{key}";

            var request = new HttpRequestMessage(HttpMethod.Head, fullPath);
            var response = await SendWithRetryAsync(request, ct);

            var size = response.Content.Headers.ContentLength ?? 0;
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            var lastModified = response.Content.Headers.LastModified?.UtcDateTime ?? DateTime.UtcNow;

            // Extract S3 metadata
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
                ETag = response.Headers.ETag?.Tag?.Trim('"') ?? string.Empty,
                ContentType = contentType,
                CustomMetadata = customMetadata.Count > 0 ? customMetadata : null,
                Tier = Tier
            };
        }

        /// <summary>
        /// Gets the current health status of the Pure Storage FlashBlade.
        /// </summary>
        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                // Check array health using FlashBlade REST API
                // API: GET /api/{version}/arrays
                var request = new HttpRequestMessage(HttpMethod.Get, $"/api/{_apiVersion}/arrays");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient!.SendAsync(request, ct);
                sw.Stop();

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    var jsonDoc = JsonDocument.Parse(json);

                    string arrayName = "Unknown";
                    if (jsonDoc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
                    {
                        var firstItem = items[0];
                        arrayName = firstItem.TryGetProperty("name", out var nameElement)
                            ? nameElement.GetString() ?? "Unknown"
                            : "Unknown";
                    }

                    // Get capacity information
                    var capacityInfo = await GetArrayCapacityAsync(ct);

                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Healthy,
                        LatencyMs = sw.Elapsed.TotalMilliseconds,
                        Message = $"FlashBlade array '{arrayName}' is accessible. Target: {(_useS3Protocol ? $"Bucket: {_bucket}" : $"FileSystem: {_fileSystem}")}",
                        AvailableCapacity = capacityInfo.Available,
                        TotalCapacity = capacityInfo.Total,
                        UsedCapacity = capacityInfo.Used,
                        CheckedAt = DateTime.UtcNow
                    };
                }
                else
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Unhealthy,
                        LatencyMs = sw.Elapsed.TotalMilliseconds,
                        Message = $"FlashBlade array returned status code {response.StatusCode}",
                        CheckedAt = DateTime.UtcNow
                    };
                }
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to access FlashBlade array: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        /// <summary>
        /// Gets the available capacity in bytes.
        /// </summary>
        protected override async Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            try
            {
                var capacityInfo = await GetArrayCapacityAsync(ct);
                return capacityInfo.Available;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PureStorageStrategy.GetAvailableCapacityAsyncCore] {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region FlashBlade-Specific Operations

        /// <summary>
        /// Gets capacity information for the FlashBlade array.
        /// </summary>
        private async Task<(long Total, long Used, long Available)> GetArrayCapacityAsync(CancellationToken ct)
        {
            // API: GET /api/{version}/arrays/space
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/{_apiVersion}/arrays/space");
            var response = await SendWithRetryAsync(request, ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            var jsonDoc = JsonDocument.Parse(json);

            if (jsonDoc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
            {
                var item = items[0];
                var capacity = item.TryGetProperty("capacity", out var capacityElement) ? capacityElement.GetInt64() : 0L;
                var space = item.TryGetProperty("space", out var spaceElement) ? spaceElement : default;

                long total = capacity;
                long used = 0L;

                if (spaceElement.ValueKind != JsonValueKind.Undefined)
                {
                    used = spaceElement.TryGetProperty("total_physical", out var usedElement) ? usedElement.GetInt64() : 0L;
                }

                var available = total - used;

                return (total, used, available);
            }

            return (0L, 0L, 0L);
        }

        /// <summary>
        /// Creates a snapshot of the file system or bucket.
        /// </summary>
        public async Task<string> CreateSnapshotAsync(string fileSystemName, string snapshotName, CancellationToken ct = default)
        {
            // API: POST /api/{version}/file-system-snapshots
            var payload = new JsonObject
            {
                ["source"] = new JsonObject
                {
                    ["name"] = fileSystemName
                },
                ["suffix"] = snapshotName
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/{_apiVersion}/file-system-snapshots")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };

            var response = await SendWithRetryAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            var jsonDoc = JsonDocument.Parse(json);

            if (jsonDoc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
            {
                var item = items[0];
                if (item.TryGetProperty("name", out var nameElement))
                {
                    return nameElement.GetString() ?? string.Empty;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Lists all snapshots for the file system.
        /// </summary>
        public async Task<IReadOnlyList<PureSnapshotInfo>> ListSnapshotsAsync(CancellationToken ct = default)
        {
            // API: GET /api/{version}/file-system-snapshots?source.name={fs}
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/{_apiVersion}/file-system-snapshots?source.name={Uri.EscapeDataString(_fileSystem!)}");
            var response = await SendWithRetryAsync(request, ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            var jsonDoc = JsonDocument.Parse(json);

            var snapshots = new List<PureSnapshotInfo>();

            if (jsonDoc.RootElement.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var name = item.GetProperty("name").GetString() ?? string.Empty;
                    var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
                    var created = item.TryGetProperty("created", out var createdElement)
                        ? DateTime.Parse(createdElement.GetString() ?? DateTime.UtcNow.ToString())
                        : DateTime.UtcNow;

                    snapshots.Add(new PureSnapshotInfo
                    {
                        Name = name,
                        Id = id,
                        CreatedTime = created
                    });
                }
            }

            return snapshots;
        }

        /// <summary>
        /// Deletes a snapshot.
        /// </summary>
        public async Task DeleteSnapshotAsync(string snapshotName, CancellationToken ct = default)
        {
            // API: DELETE /api/{version}/file-system-snapshots?names={name}
            var request = new HttpRequestMessage(HttpMethod.Delete,
                $"/api/{_apiVersion}/file-system-snapshots?names={Uri.EscapeDataString(snapshotName)}");
            await SendWithRetryAsync(request, ct);
        }

        /// <summary>
        /// Triggers replication for the file system.
        /// </summary>
        public async Task TriggerReplicationAsync(string fileSystemName, CancellationToken ct = default)
        {
            // API: POST /api/{version}/file-system-replica-links/{id}/transfer
            // First, find the replica link ID for this file system
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/{_apiVersion}/file-system-replica-links?local_file_system.name={Uri.EscapeDataString(fileSystemName)}");
            var response = await SendWithRetryAsync(request, ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            var jsonDoc = JsonDocument.Parse(json);

            if (jsonDoc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
            {
                var item = items[0];
                var linkId = item.GetProperty("id").GetString() ?? string.Empty;

                // Trigger replication
                var transferRequest = new HttpRequestMessage(HttpMethod.Post,
                    $"/api/{_apiVersion}/file-system-replica-links/{linkId}/transfer")
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
                await SendWithRetryAsync(transferRequest, ct);
            }
        }

        /// <summary>
        /// Applies SafeMode protection to a file (immutability).
        /// </summary>
        public async Task ApplySafeModeAsync(string filePath, int retentionDays, CancellationToken ct = default)
        {
            // API: PATCH /api/{version}/files?names={name}
            var retainUntil = DateTime.UtcNow.AddDays(retentionDays);

            var payload = new JsonObject
            {
                ["retention_lock"] = "locked",
                ["retention_until"] = retainUntil.ToString("o")
            };

            var request = new HttpRequestMessage(HttpMethod.Patch,
                $"/api/{_apiVersion}/files?names={Uri.EscapeDataString(filePath)}")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };

            await SendWithRetryAsync(request, ct);
        }

        /// <summary>
        /// Applies directory quotas to a path.
        /// </summary>
        public async Task ApplyQuotaAsync(string path, long hardLimitBytes, CancellationToken ct = default)
        {
            // API: POST /api/{version}/quotas/users
            var payload = new JsonObject
            {
                ["file_system"] = new JsonObject
                {
                    ["name"] = _fileSystem
                },
                ["path"] = path,
                ["quota"] = hardLimitBytes
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/{_apiVersion}/quotas/users")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };

            await SendWithRetryAsync(request, ct);
        }

        /// <summary>
        /// Configures bucket lifecycle rules.
        /// </summary>
        public async Task ConfigureLifecycleRuleAsync(string ruleName, int transitionDays, string targetTier, CancellationToken ct = default)
        {
            // API: POST /api/{version}/object-store-buckets/{bucket}/lifecycle-rules
            var payload = new JsonObject
            {
                ["rule_id"] = ruleName,
                ["keep_current_version_until"] = transitionDays,
                ["tier"] = targetTier
            };

            var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/{_apiVersion}/object-store-buckets/{_bucket}/lifecycle-rules")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };

            await SendWithRetryAsync(request, ct);
        }

        /// <summary>
        /// Configures NFS export rules.
        /// </summary>
        public async Task ConfigureNfsExportAsync(string exportPath, string rules, CancellationToken ct = default)
        {
            // API: POST /api/{version}/nfs-exports
            var payload = new JsonObject
            {
                ["file_system"] = new JsonObject
                {
                    ["name"] = _fileSystem
                },
                ["path"] = exportPath,
                ["rules"] = rules
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/{_apiVersion}/nfs-exports")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };

            await SendWithRetryAsync(request, ct);
        }

        /// <summary>
        /// Configures SMB share settings.
        /// </summary>
        public async Task ConfigureSmbShareAsync(string shareName, string path, CancellationToken ct = default)
        {
            // API: POST /api/{version}/smb-shares
            var payload = new JsonObject
            {
                ["name"] = shareName,
                ["file_system"] = new JsonObject
                {
                    ["name"] = _fileSystem
                },
                ["path"] = path
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/{_apiVersion}/smb-shares")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };

            await SendWithRetryAsync(request, ct);
        }

        /// <summary>
        /// Gets performance metrics for the array.
        /// </summary>
        public async Task<PerformanceMetrics> GetPerformanceMetricsAsync(CancellationToken ct = default)
        {
            // API: GET /api/{version}/arrays/performance
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/{_apiVersion}/arrays/performance");
            var response = await SendWithRetryAsync(request, ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            var jsonDoc = JsonDocument.Parse(json);

            if (jsonDoc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
            {
                var item = items[0];

                return new PerformanceMetrics
                {
                    ReadBytesPerSecond = item.TryGetProperty("read_bytes_per_sec", out var readElement) ? readElement.GetInt64() : 0L,
                    WriteBytesPerSecond = item.TryGetProperty("write_bytes_per_sec", out var writeElement) ? writeElement.GetInt64() : 0L,
                    ReadOpsPerSecond = item.TryGetProperty("reads_per_sec", out var readOpsElement) ? readOpsElement.GetInt64() : 0L,
                    WriteOpsPerSecond = item.TryGetProperty("writes_per_sec", out var writeOpsElement) ? writeOpsElement.GetInt64() : 0L,
                    AverageReadLatencyMs = item.TryGetProperty("usec_per_read_op", out var readLatElement) ? readLatElement.GetDouble() / 1000.0 : 0.0,
                    AverageWriteLatencyMs = item.TryGetProperty("usec_per_write_op", out var writeLatElement) ? writeLatElement.GetDouble() / 1000.0 : 0.0,
                    Timestamp = DateTime.UtcNow
                };
            }

            return new PerformanceMetrics();
        }

        #endregion

        #region Helper Methods

        private string GetFullPath(string key)
        {
            return $"{_basePath}/{key}".Replace("//", "/");
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

        /// <summary>
        /// Generates a non-cryptographic ETag. AD-11: crypto hashing via UltimateDataIntegrity bus.
        /// </summary>
        private string CalculateETag(string input)
        {
            return HashCode.Combine(input).ToString("x8");
        }

        /// <summary>
        /// Generates a non-cryptographic ETag. AD-11: crypto hashing via UltimateDataIntegrity bus.
        /// </summary>
        private string CalculateETag(byte[] data)
        {
            var hash = new HashCode();
            hash.AddBytes(data);
            return hash.ToHashCode().ToString("x8");
        }

        /// <summary>
        /// Gets the maximum allowed key length for Pure Storage FlashBlade.
        /// </summary>
        protected override int GetMaxKeyLength() => 4096; // FlashBlade supports long paths

        #endregion

        #region Cleanup

        /// <summary>
        /// Disposes resources used by this storage strategy.
        /// </summary>
        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();
            _httpClient?.Dispose();
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Information about a Pure Storage FlashBlade snapshot.
    /// </summary>
    public record PureSnapshotInfo
    {
        /// <summary>Snapshot name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Snapshot unique identifier.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>When the snapshot was created.</summary>
        public DateTime CreatedTime { get; init; }
    }

    /// <summary>
    /// Performance metrics for Pure Storage FlashBlade.
    /// </summary>
    public record PerformanceMetrics
    {
        /// <summary>Read throughput in bytes per second.</summary>
        public long ReadBytesPerSecond { get; init; }

        /// <summary>Write throughput in bytes per second.</summary>
        public long WriteBytesPerSecond { get; init; }

        /// <summary>Read operations per second.</summary>
        public long ReadOpsPerSecond { get; init; }

        /// <summary>Write operations per second.</summary>
        public long WriteOpsPerSecond { get; init; }

        /// <summary>Average read latency in milliseconds.</summary>
        public double AverageReadLatencyMs { get; init; }

        /// <summary>Average write latency in milliseconds.</summary>
        public double AverageWriteLatencyMs { get; init; }

        /// <summary>When these metrics were captured.</summary>
        public DateTime Timestamp { get; init; }
    }

    #endregion
}
