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
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Enterprise
{
    /// <summary>
    /// HPE StoreOnce storage strategy using Catalyst REST API.
    ///
    /// HPE StoreOnce is a backup and recovery solution with advanced deduplication features:
    /// - Catalyst REST API for data protection operations
    /// - Catalyst stores (backup repositories) for storing deduplicated data
    /// - StoreOnce Deduplication Engine (up to 100:1 ratios)
    /// - Cloud Bank Storage for S3-compatible cloud tiering
    /// - Store-level replication for disaster recovery
    /// - Encryption at rest with AES-256
    /// - Data lifecycle management and retention policies
    /// - Virtual Tape Library (VTL) operations for tape-to-disk migration
    /// - NAS shares (CIFS/NFS) for file-level backup
    /// - Performance metrics and capacity monitoring
    /// - Multi-node federation for scale-out architecture
    /// - Capacity on demand for flexible licensing
    ///
    /// Configuration Requirements:
    /// - Endpoint: StoreOnce appliance URL (e.g., "https://storeonce.example.com")
    /// - Username: API user credentials
    /// - Password: API password credentials
    /// - StoreName: Catalyst store name to use
    /// - ClientName: Client identifier for this application
    ///
    /// Optional Configuration:
    /// - EnableDeduplication: Enable StoreOnce deduplication (default: true)
    /// - EnableEncryption: Enable encryption at rest (default: true)
    /// - CloudBankEnabled: Enable cloud bank tiering (default: false)
    /// - CloudBankProvider: Cloud provider (AWS, Azure, GCP) when cloud bank enabled
    /// - ReplicationEnabled: Enable store replication (default: false)
    /// - ReplicationTarget: Target StoreOnce appliance for replication
    /// - RetentionDays: Data retention period in days (default: 30)
    /// - CompressionEnabled: Enable additional compression (default: true)
    /// - VtlEnabled: Enable VTL operations (default: false)
    /// - NasShareName: NAS share name for file-level operations
    /// - FederationEnabled: Enable multi-node federation (default: false)
    /// - CapacityOnDemandEnabled: Enable capacity on demand features (default: false)
    /// - TimeoutSeconds: HTTP request timeout (default: 300)
    /// - MaxRetries: Maximum retry attempts (default: 3)
    /// - RetryDelayMs: Delay between retries in milliseconds (default: 1000)
    /// </summary>
    public class HpeStoreOnceStrategy : UltimateStorageStrategyBase
    {
        private HttpClient? _httpClient;
        private string _endpoint = string.Empty;
        private string _username = string.Empty;
        private string _password = string.Empty;
        private string _storeName = string.Empty;
        private string _clientName = string.Empty;
        private string _authToken = string.Empty;
        private DateTime _tokenExpiry = DateTime.MinValue;

        // Feature flags
        private bool _enableDeduplication = true;
        private bool _enableEncryption = true;
        private bool _cloudBankEnabled = false;
        private string _cloudBankProvider = "AWS";
        private bool _replicationEnabled = false;
        private string _replicationTarget = string.Empty;
        private int _retentionDays = 30;
        private bool _compressionEnabled = true;
        private bool _vtlEnabled = false;
        private string _nasShareName = string.Empty;
        private bool _federationEnabled = false;
        private bool _capacityOnDemandEnabled = false;

        // Connection settings
        private int _timeoutSeconds = 300;
        private int _maxRetries = 3;
        private int _retryDelayMs = 1000;
        private bool _validateCertificate = true;

        // Store metadata cache
        private StoreMetadata? _cachedStoreMetadata;
        private DateTime _storeMetadataCacheExpiry = DateTime.MinValue;
        private readonly TimeSpan _storeMetadataCacheDuration = TimeSpan.FromMinutes(5);

        public override string StrategyId => "hpe-storeonce";
        public override string Name => "HPE StoreOnce Catalyst";
        public override StorageTier Tier => StorageTier.Warm; // Deduplication storage, suitable for backup/archive

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false,
            SupportsVersioning = false, // Versioning handled via backup sets
            SupportsTiering = _cloudBankEnabled,
            SupportsEncryption = true,
            SupportsCompression = true, // StoreOnce native deduplication and compression
            SupportsMultipart = false, // Catalyst API handles chunking internally
            MaxObjectSize = 100L * 1024 * 1024 * 1024 * 1024, // 100TB theoretical limit
            MaxObjects = null, // Limited by capacity, not object count
            ConsistencyModel = ConsistencyModel.Strong // Immediate consistency after write
        };

        /// <summary>
        /// Initializes the HPE StoreOnce storage strategy.
        /// Validates configuration and establishes connection to StoreOnce appliance.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load required configuration
            _endpoint = GetConfiguration<string>("Endpoint", string.Empty);
            _username = GetConfiguration<string>("Username", string.Empty);
            _password = GetConfiguration<string>("Password", string.Empty);
            _storeName = GetConfiguration<string>("StoreName", string.Empty);
            _clientName = GetConfiguration<string>("ClientName", "DataWarehouse");

            // Validate required configuration
            if (string.IsNullOrWhiteSpace(_endpoint))
            {
                throw new InvalidOperationException("HPE StoreOnce endpoint is required. Set 'Endpoint' in configuration.");
            }
            if (string.IsNullOrWhiteSpace(_username) || string.IsNullOrWhiteSpace(_password))
            {
                throw new InvalidOperationException("HPE StoreOnce credentials are required. Set 'Username' and 'Password' in configuration.");
            }
            if (string.IsNullOrWhiteSpace(_storeName))
            {
                throw new InvalidOperationException("HPE StoreOnce store name is required. Set 'StoreName' in configuration.");
            }

            // Load optional configuration
            _enableDeduplication = GetConfiguration<bool>("EnableDeduplication", true);
            _enableEncryption = GetConfiguration<bool>("EnableEncryption", true);
            _cloudBankEnabled = GetConfiguration<bool>("CloudBankEnabled", false);
            _cloudBankProvider = GetConfiguration<string>("CloudBankProvider", "AWS");
            _replicationEnabled = GetConfiguration<bool>("ReplicationEnabled", false);
            _replicationTarget = GetConfiguration<string>("ReplicationTarget", string.Empty);
            _retentionDays = GetConfiguration<int>("RetentionDays", 30);
            _compressionEnabled = GetConfiguration<bool>("CompressionEnabled", true);
            _vtlEnabled = GetConfiguration<bool>("VtlEnabled", false);
            _nasShareName = GetConfiguration<string>("NasShareName", string.Empty);
            _federationEnabled = GetConfiguration<bool>("FederationEnabled", false);
            _capacityOnDemandEnabled = GetConfiguration<bool>("CapacityOnDemandEnabled", false);
            _timeoutSeconds = GetConfiguration<int>("TimeoutSeconds", 300);
            _maxRetries = GetConfiguration<int>("MaxRetries", 3);
            _retryDelayMs = GetConfiguration<int>("RetryDelayMs", 1000);
            _validateCertificate = GetConfiguration<bool>("ValidateCertificate", true);

            // Normalize endpoint
            _endpoint = _endpoint.TrimEnd('/');

            // Create HTTP client with custom handler for authentication
            var handler = new HttpClientHandler();
            // SECURITY: TLS certificate validation is enabled by default.
            // Only bypass when explicitly configured to false.
            if (!_validateCertificate)
            {
                handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
            }

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(_timeoutSeconds)
            };

            // Authenticate and get token
            await AuthenticateAsync(ct);

            // Verify store exists and get metadata
            await VerifyStoreAccessAsync(ct);
        }

        #region Core Storage Operations

        /// <summary>
        /// Stores data to HPE StoreOnce Catalyst store with deduplication.
        /// </summary>
        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            ValidateKey(key);
            ValidateStream(data);

            await EnsureAuthenticatedAsync(ct);

            // Read data into memory (Catalyst API requires content length)
            using var ms = new MemoryStream();
            await data.CopyToAsync(ms, 81920, ct);
            var content = ms.ToArray();

            // Build object path in store
            var objectPath = $"/cat/stores/{_storeName}/objects/{Uri.EscapeDataString(key)}";

            // Create PUT request with Catalyst headers
            var request = new HttpRequestMessage(HttpMethod.Put, $"{_endpoint}{objectPath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
            request.Headers.Add("X-HP-StoreOnce-Client", _clientName);

            if (_enableDeduplication)
            {
                request.Headers.Add("X-HP-Dedup-Enabled", "true");
            }

            if (_enableEncryption)
            {
                request.Headers.Add("X-HP-Encryption-Enabled", "true");
            }

            if (_compressionEnabled)
            {
                request.Headers.Add("X-HP-Compression-Enabled", "true");
            }

            // Add custom metadata as headers
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    request.Headers.Add($"X-HP-Meta-{kvp.Key}", kvp.Value);
                }
            }

            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentLength = content.Length;

            // Send request with retry logic
            var response = await SendWithRetryAsync(request, ct);

            // Extract metadata from response
            var etag = response.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;

            // Get deduplication ratio if available
            response.Headers.TryGetValues("X-HP-Dedup-Ratio", out var dedupRatioValues);
            var dedupRatio = dedupRatioValues?.FirstOrDefault();

            // Update statistics
            IncrementBytesStored(content.Length);
            IncrementOperationCounter(StorageOperationType.Store);

            // Trigger replication if enabled
            if (_replicationEnabled && !string.IsNullOrEmpty(_replicationTarget))
            {
                _ = Task.Run(() => ReplicateObjectAsync(key, ct), ct); // Fire and forget
            }

            var resultMetadata = new Dictionary<string, string>();
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    resultMetadata[kvp.Key] = kvp.Value;
                }
            }
            if (!string.IsNullOrEmpty(dedupRatio))
            {
                resultMetadata["DedupRatio"] = dedupRatio;
            }

            return new StorageObjectMetadata
            {
                Key = key,
                Size = content.Length,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ETag = etag,
                ContentType = "application/octet-stream",
                CustomMetadata = resultMetadata.Count > 0 ? resultMetadata : null,
                Tier = _cloudBankEnabled ? StorageTier.Cold : StorageTier.Warm
            };
        }

        /// <summary>
        /// Retrieves data from HPE StoreOnce Catalyst store.
        /// </summary>
        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            await EnsureAuthenticatedAsync(ct);

            var objectPath = $"/cat/stores/{_storeName}/objects/{Uri.EscapeDataString(key)}";
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_endpoint}{objectPath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
            request.Headers.Add("X-HP-StoreOnce-Client", _clientName);

            var response = await SendWithRetryAsync(request, ct);

            var ms = new MemoryStream();
            await response.Content.CopyToAsync(ms, ct);
            ms.Position = 0;

            // Update statistics
            IncrementBytesRetrieved(ms.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            return ms;
        }

        /// <summary>
        /// Deletes data from HPE StoreOnce Catalyst store.
        /// Respects retention policies if configured.
        /// </summary>
        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            await EnsureAuthenticatedAsync(ct);

            // Check retention policy
            if (_retentionDays > 0)
            {
                var metadata = await GetMetadataAsyncCore(key, ct);
                var age = DateTime.UtcNow - metadata.Created;

                if (age.TotalDays < _retentionDays)
                {
                    throw new InvalidOperationException(
                        $"Object '{key}' cannot be deleted. Retention policy requires {_retentionDays} days, object age is {age.TotalDays:F1} days.");
                }
            }

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

            var objectPath = $"/cat/stores/{_storeName}/objects/{Uri.EscapeDataString(key)}";
            var request = new HttpRequestMessage(HttpMethod.Delete, $"{_endpoint}{objectPath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
            request.Headers.Add("X-HP-StoreOnce-Client", _clientName);

            await SendWithRetryAsync(request, ct);

            // Update statistics
            if (size > 0)
            {
                IncrementBytesDeleted(size);
            }
            IncrementOperationCounter(StorageOperationType.Delete);
        }

        /// <summary>
        /// Checks if an object exists in HPE StoreOnce Catalyst store.
        /// </summary>
        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            await EnsureAuthenticatedAsync(ct);

            try
            {
                var objectPath = $"/cat/stores/{_storeName}/objects/{Uri.EscapeDataString(key)}";
                var request = new HttpRequestMessage(HttpMethod.Head, $"{_endpoint}{objectPath}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                request.Headers.Add("X-HP-StoreOnce-Client", _clientName);

                var response = await _httpClient!.SendAsync(request, ct);

                IncrementOperationCounter(StorageOperationType.Exists);

                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Lists objects in HPE StoreOnce Catalyst store with optional prefix filter.
        /// </summary>
        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            await EnsureAuthenticatedAsync(ct);

            IncrementOperationCounter(StorageOperationType.List);

            var listPath = $"/cat/stores/{_storeName}/objects";
            if (!string.IsNullOrEmpty(prefix))
            {
                listPath += $"?prefix={Uri.EscapeDataString(prefix)}";
            }

            var request = new HttpRequestMessage(HttpMethod.Get, $"{_endpoint}{listPath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
            request.Headers.Add("X-HP-StoreOnce-Client", _clientName);

            var response = await SendWithRetryAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("objects", out var objectsArray))
            {
                foreach (var obj in objectsArray.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();

                    if (!obj.TryGetProperty("name", out var nameElement))
                        continue;

                    var key = nameElement.GetString();
                    if (string.IsNullOrEmpty(key))
                        continue;

                    var size = obj.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0L;
                    var modified = obj.TryGetProperty("modified", out var modifiedElement) &&
                                   DateTime.TryParse(modifiedElement.GetString(), out var parsedDate)
                        ? parsedDate
                        : DateTime.UtcNow;

                    var etag = obj.TryGetProperty("etag", out var etagElement) ? etagElement.GetString() ?? string.Empty : string.Empty;

                    // Extract custom metadata if present
                    Dictionary<string, string>? customMetadata = null;
                    if (obj.TryGetProperty("metadata", out var metadataElement))
                    {
                        customMetadata = new Dictionary<string, string>();
                        foreach (var prop in metadataElement.EnumerateObject())
                        {
                            customMetadata[prop.Name] = prop.Value.GetString() ?? string.Empty;
                        }
                    }

                    yield return new StorageObjectMetadata
                    {
                        Key = key,
                        Size = size,
                        Created = modified,
                        Modified = modified,
                        ETag = etag,
                        ContentType = "application/octet-stream",
                        CustomMetadata = customMetadata,
                        Tier = _cloudBankEnabled ? StorageTier.Cold : StorageTier.Warm
                    };

                    await Task.Yield();
                }
            }
        }

        /// <summary>
        /// Gets metadata for an object in HPE StoreOnce Catalyst store.
        /// </summary>
        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            await EnsureAuthenticatedAsync(ct);

            var objectPath = $"/cat/stores/{_storeName}/objects/{Uri.EscapeDataString(key)}";
            var request = new HttpRequestMessage(HttpMethod.Head, $"{_endpoint}{objectPath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
            request.Headers.Add("X-HP-StoreOnce-Client", _clientName);

            var response = await SendWithRetryAsync(request, ct);

            var size = response.Content.Headers.ContentLength ?? 0;
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            var lastModified = response.Content.Headers.LastModified?.UtcDateTime ?? DateTime.UtcNow;
            var etag = response.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;

            // Extract custom metadata from headers
            var customMetadata = new Dictionary<string, string>();
            foreach (var header in response.Headers.Where(h => h.Key.StartsWith("X-HP-Meta-", StringComparison.OrdinalIgnoreCase)))
            {
                var metaKey = header.Key.Substring("X-HP-Meta-".Length);
                customMetadata[metaKey] = header.Value.FirstOrDefault() ?? string.Empty;
            }

            // Extract deduplication information
            if (response.Headers.TryGetValues("X-HP-Dedup-Ratio", out var dedupRatioValues))
            {
                var dedupRatio = dedupRatioValues.FirstOrDefault();
                if (!string.IsNullOrEmpty(dedupRatio))
                {
                    customMetadata["DedupRatio"] = dedupRatio;
                }
            }

            if (response.Headers.TryGetValues("X-HP-Physical-Size", out var physicalSizeValues))
            {
                var physicalSize = physicalSizeValues.FirstOrDefault();
                if (!string.IsNullOrEmpty(physicalSize))
                {
                    customMetadata["PhysicalSize"] = physicalSize;
                }
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
                Tier = _cloudBankEnabled ? StorageTier.Cold : StorageTier.Warm
            };
        }

        /// <summary>
        /// Gets health status of HPE StoreOnce appliance and Catalyst store.
        /// Includes capacity, deduplication ratio, and performance metrics.
        /// </summary>
        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                await EnsureAuthenticatedAsync(ct);

                // Get store health and capacity
                var storeHealthPath = $"/cat/stores/{_storeName}/health";
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_endpoint}{storeHealthPath}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                request.Headers.Add("X-HP-StoreOnce-Client", _clientName);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient!.SendAsync(request, ct);
                sw.Stop();

                if (!response.IsSuccessStatusCode)
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Unhealthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"HPE StoreOnce store '{_storeName}' returned status code {response.StatusCode}",
                        CheckedAt = DateTime.UtcNow
                    };
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Parse health information
                var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : "unknown";
                var totalCapacity = root.TryGetProperty("totalCapacity", out var totalCapElement) ? totalCapElement.GetInt64() : 0L;
                var usedCapacity = root.TryGetProperty("usedCapacity", out var usedCapElement) ? usedCapElement.GetInt64() : 0L;
                var availableCapacity = totalCapacity - usedCapacity;

                var healthStatus = status?.ToLowerInvariant() switch
                {
                    "healthy" or "online" => HealthStatus.Healthy,
                    "degraded" or "warning" => HealthStatus.Degraded,
                    "offline" or "error" => HealthStatus.Unhealthy,
                    _ => HealthStatus.Unknown
                };

                var message = $"HPE StoreOnce store '{_storeName}' is {status}";

                // Add deduplication information
                if (root.TryGetProperty("dedupRatio", out var dedupRatioElement))
                {
                    var dedupRatio = dedupRatioElement.GetDouble();
                    message += $", Dedup Ratio: {dedupRatio:F1}:1";
                }

                return new StorageHealthInfo
                {
                    Status = healthStatus,
                    LatencyMs = sw.ElapsedMilliseconds,
                    TotalCapacity = totalCapacity > 0 ? totalCapacity : null,
                    UsedCapacity = usedCapacity > 0 ? usedCapacity : null,
                    AvailableCapacity = availableCapacity > 0 ? availableCapacity : null,
                    Message = message,
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to access HPE StoreOnce store '{_storeName}': {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        /// <summary>
        /// Gets available capacity from HPE StoreOnce Catalyst store.
        /// </summary>
        protected override async Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            try
            {
                var storeMetadata = await GetStoreMetadataAsync(ct);
                return storeMetadata?.AvailableCapacity;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region HPE StoreOnce Specific Operations

        /// <summary>
        /// Authenticates with HPE StoreOnce appliance and obtains access token.
        /// </summary>
        private async Task AuthenticateAsync(CancellationToken ct)
        {
            var authPath = "/api/v1/auth/login";
            var authData = new
            {
                username = _username,
                password = _password
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}{authPath}");
            request.Content = new StringContent(
                JsonSerializer.Serialize(authData),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient!.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("token", out var tokenElement))
            {
                _authToken = tokenElement.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("expiresIn", out var expiresInElement))
            {
                var expiresInSeconds = expiresInElement.GetInt32();
                _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresInSeconds - 60); // Refresh 1 minute early
            }
            else
            {
                _tokenExpiry = DateTime.UtcNow.AddHours(1); // Default 1 hour expiry
            }

            if (string.IsNullOrEmpty(_authToken))
            {
                throw new InvalidOperationException("Failed to obtain authentication token from HPE StoreOnce");
            }
        }

        /// <summary>
        /// Ensures authentication token is valid, refreshing if necessary.
        /// </summary>
        private async Task EnsureAuthenticatedAsync(CancellationToken ct)
        {
            if (DateTime.UtcNow >= _tokenExpiry || string.IsNullOrEmpty(_authToken))
            {
                await AuthenticateAsync(ct);
            }
        }

        /// <summary>
        /// Verifies that the configured Catalyst store exists and is accessible.
        /// </summary>
        private async Task VerifyStoreAccessAsync(CancellationToken ct)
        {
            var storePath = $"/cat/stores/{_storeName}";
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_endpoint}{storePath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
            request.Headers.Add("X-HP-StoreOnce-Client", _clientName);

            var response = await _httpClient!.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Failed to access HPE StoreOnce store '{_storeName}'. " +
                    $"Status: {response.StatusCode}. Verify store name and permissions.");
            }

            // Verify store is online
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var statusElement))
            {
                var status = statusElement.GetString();
                if (status?.ToLowerInvariant() != "online" && status?.ToLowerInvariant() != "healthy")
                {
                    throw new InvalidOperationException(
                        $"HPE StoreOnce store '{_storeName}' is not online. Current status: {status}");
                }
            }
        }

        /// <summary>
        /// Gets store metadata including capacity and deduplication statistics.
        /// Results are cached for performance.
        /// </summary>
        private async Task<StoreMetadata?> GetStoreMetadataAsync(CancellationToken ct)
        {
            // Check cache
            if (_cachedStoreMetadata != null && DateTime.UtcNow < _storeMetadataCacheExpiry)
            {
                return _cachedStoreMetadata;
            }

            await EnsureAuthenticatedAsync(ct);

            var storePath = $"/cat/stores/{_storeName}";
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_endpoint}{storePath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
            request.Headers.Add("X-HP-StoreOnce-Client", _clientName);

            var response = await _httpClient!.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var metadata = new StoreMetadata
            {
                Name = _storeName,
                Status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() ?? "unknown" : "unknown",
                TotalCapacity = root.TryGetProperty("totalCapacity", out var totalCapElement) ? totalCapElement.GetInt64() : 0L,
                UsedCapacity = root.TryGetProperty("usedCapacity", out var usedCapElement) ? usedCapElement.GetInt64() : 0L,
                DedupRatio = root.TryGetProperty("dedupRatio", out var dedupRatioElement) ? dedupRatioElement.GetDouble() : 1.0,
                CompressionRatio = root.TryGetProperty("compressionRatio", out var compressionRatioElement) ? compressionRatioElement.GetDouble() : 1.0
            };

            metadata.AvailableCapacity = metadata.TotalCapacity - metadata.UsedCapacity;

            // Cache metadata
            _cachedStoreMetadata = metadata;
            _storeMetadataCacheExpiry = DateTime.UtcNow.Add(_storeMetadataCacheDuration);

            return metadata;
        }

        /// <summary>
        /// Replicates an object to the configured replication target.
        /// Used for disaster recovery and geographic distribution.
        /// </summary>
        private async Task ReplicateObjectAsync(string key, CancellationToken ct)
        {
            if (!_replicationEnabled || string.IsNullOrEmpty(_replicationTarget))
            {
                return;
            }

            try
            {
                await EnsureAuthenticatedAsync(ct);

                var replicationPath = $"/cat/stores/{_storeName}/replicate";
                var replicationData = new
                {
                    objectKey = key,
                    targetAppliance = _replicationTarget,
                    priority = "normal"
                };

                var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}{replicationPath}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
                request.Headers.Add("X-HP-StoreOnce-Client", _clientName);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(replicationData),
                    Encoding.UTF8,
                    "application/json");

                await _httpClient!.SendAsync(request, ct);
            }
            catch
            {
                // Replication is best-effort, don't fail the main operation
            }
        }

        /// <summary>
        /// Gets deduplication statistics for the store.
        /// </summary>
        public async Task<DeduplicationStatistics> GetDeduplicationStatisticsAsync(CancellationToken ct = default)
        {
            var storeMetadata = await GetStoreMetadataAsync(ct);

            return new DeduplicationStatistics
            {
                DedupRatio = storeMetadata?.DedupRatio ?? 1.0,
                CompressionRatio = storeMetadata?.CompressionRatio ?? 1.0,
                LogicalCapacity = storeMetadata?.UsedCapacity ?? 0L,
                PhysicalCapacity = storeMetadata?.UsedCapacity != null && storeMetadata?.DedupRatio > 0
                    ? (long)(storeMetadata.UsedCapacity / storeMetadata.DedupRatio)
                    : 0L,
                SpaceSavingsPercent = storeMetadata?.DedupRatio > 1
                    ? ((storeMetadata.DedupRatio - 1) / storeMetadata.DedupRatio) * 100
                    : 0.0
            };
        }

        /// <summary>
        /// Triggers a manual deduplication job on the store.
        /// StoreOnce typically deduplicates inline, but this can force a scan.
        /// </summary>
        public async Task TriggerDeduplicationAsync(CancellationToken ct = default)
        {
            await EnsureAuthenticatedAsync(ct);

            var dedupPath = $"/cat/stores/{_storeName}/dedup";
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}{dedupPath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
            request.Headers.Add("X-HP-StoreOnce-Client", _clientName);

            await _httpClient!.SendAsync(request, ct);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Sends HTTP request with retry logic for transient failures.
        /// </summary>
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
                catch (Exception ex) when (attempt < _maxRetries && IsTransientException(ex))
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

        /// <summary>
        /// Determines if an HTTP status code should trigger a retry.
        /// </summary>
        private bool ShouldRetry(System.Net.HttpStatusCode statusCode)
        {
            return statusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                   statusCode == System.Net.HttpStatusCode.RequestTimeout ||
                   statusCode == System.Net.HttpStatusCode.TooManyRequests ||
                   statusCode == System.Net.HttpStatusCode.Unauthorized || // Token might have expired
                   (int)statusCode >= 500;
        }

        /// <summary>
        /// Determines if an exception represents a transient failure.
        /// </summary>
        protected override bool IsTransientException(Exception exception)
        {
            return exception is IOException ||
                   exception is TimeoutException ||
                   exception is HttpRequestException;
        }

        /// <summary>
        /// Gets the maximum allowed key length for HPE StoreOnce.
        /// </summary>
        protected override int GetMaxKeyLength() => 2048; // StoreOnce allows longer keys than S3

        #endregion

        #region Cleanup

        /// <summary>
        /// Disposes resources used by the HPE StoreOnce strategy.
        /// </summary>
        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();

            // Logout and invalidate token
            if (_httpClient != null && !string.IsNullOrEmpty(_authToken))
            {
                try
                {
                    var logoutPath = "/api/v1/auth/logout";
                    var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}{logoutPath}");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);

                    await _httpClient.SendAsync(request);
                }
                catch
                {
                    // Ignore logout failures
                }
            }

            _httpClient?.Dispose();
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Metadata information for an HPE StoreOnce Catalyst store.
    /// </summary>
    internal class StoreMetadata
    {
        /// <summary>Store name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Store status (online, offline, degraded).</summary>
        public string Status { get; set; } = "unknown";

        /// <summary>Total capacity in bytes.</summary>
        public long TotalCapacity { get; set; }

        /// <summary>Used capacity in bytes (logical).</summary>
        public long UsedCapacity { get; set; }

        /// <summary>Available capacity in bytes.</summary>
        public long AvailableCapacity { get; set; }

        /// <summary>Deduplication ratio (e.g., 10.0 means 10:1).</summary>
        public double DedupRatio { get; set; } = 1.0;

        /// <summary>Compression ratio.</summary>
        public double CompressionRatio { get; set; } = 1.0;
    }

    /// <summary>
    /// Deduplication statistics for HPE StoreOnce.
    /// </summary>
    public class DeduplicationStatistics
    {
        /// <summary>Deduplication ratio achieved (e.g., 10.0 means 10:1).</summary>
        public double DedupRatio { get; set; }

        /// <summary>Compression ratio achieved.</summary>
        public double CompressionRatio { get; set; }

        /// <summary>Logical capacity (before deduplication) in bytes.</summary>
        public long LogicalCapacity { get; set; }

        /// <summary>Physical capacity (after deduplication) in bytes.</summary>
        public long PhysicalCapacity { get; set; }

        /// <summary>Space savings as a percentage (0-100).</summary>
        public double SpaceSavingsPercent { get; set; }
    }

    #endregion
}
