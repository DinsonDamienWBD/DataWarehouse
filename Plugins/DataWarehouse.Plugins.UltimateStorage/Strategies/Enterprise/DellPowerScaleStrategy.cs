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
    /// Dell EMC PowerScale (formerly Isilon) storage strategy using OneFS Platform API (PAPI).
    /// Provides enterprise-grade distributed scale-out NAS storage with advanced data management capabilities.
    /// </summary>
    /// <remarks>
    /// Key Features:
    /// - OneFS Platform API (PAPI) for RESTful operations
    /// - Access Zones for multi-tenancy and namespace isolation
    /// - SmartConnect for DNS-based load balancing across cluster nodes
    /// - SmartPools for automated tiering policies (SSD/HDD optimization)
    /// - SmartQuotas for capacity management and enforcement
    /// - SnapshotIQ for point-in-time recovery and versioning
    /// - SyncIQ for asynchronous replication across clusters
    /// - CloudPools for automated cloud tiering (AWS, Azure, GCS)
    /// - Data Protection with FlexProtect/FEC for RAID-like reliability
    /// - File filtering for content-based policies
    /// - NDMP backup integration for enterprise backup tools
    /// - Multi-protocol support: NFS, SMB, HDFS, S3
    /// - SmartLock for WORM compliance and retention policies
    /// - SmartDedupe for inline/post-process deduplication
    /// - InsightIQ monitoring integration for performance analytics
    /// </remarks>
    public class DellPowerScaleStrategy : UltimateStorageStrategyBase
    {
        private HttpClient? _httpClient;
        private string _endpoint = string.Empty;
        private string _username = string.Empty;
        private string _password = string.Empty;
        private string _accessZone = "System";
        private string _namespace = "/ifs";
        private string _storagePool = "default";
        private bool _useSmartConnect = false;
        private bool _enableSnapshotIq = false;
        private bool _enableSyncIq = false;
        private bool _enableCloudPools = false;
        private bool _enableSmartLock = false;
        private bool _enableSmartDedupe = false;
        private bool _enableSmartQuotas = false;
        private int _timeoutSeconds = 120;
        private int _maxRetries = 3;
        private int _retryDelayMs = 1000;
        private string? _authToken;
        private DateTime _authTokenExpiry = DateTime.MinValue;
        private readonly SemaphoreSlim _authLock = new(1, 1);

        /// <summary>
        /// Gets the unique identifier for this storage strategy.
        /// </summary>
        public override string StrategyId => "dell-powerscale";

        /// <summary>
        /// Gets the human-readable name of this storage strategy.
        /// </summary>
        public override string Name => "Dell EMC PowerScale (Isilon OneFS)";

        /// <summary>
        /// Gets the storage tier this strategy operates on.
        /// PowerScale supports multiple tiers through SmartPools.
        /// </summary>
        public override StorageTier Tier => StorageTier.Warm;

        /// <summary>
        /// Gets the capabilities supported by PowerScale storage.
        /// </summary>
        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = true, // SmartLock WORM support
            SupportsVersioning = true, // SnapshotIQ provides versioning
            SupportsTiering = true, // SmartPools automated tiering
            SupportsEncryption = true, // At-rest and in-flight encryption
            SupportsCompression = true, // SmartDedupe includes compression
            SupportsMultipart = true,
            MaxObjectSize = 16L * 1024 * 1024 * 1024 * 1024, // 16TB per file
            MaxObjects = null, // No practical limit
            ConsistencyModel = ConsistencyModel.Strong // OneFS provides strong consistency
        };

        /// <summary>
        /// Initializes the Dell PowerScale storage strategy.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load required configuration
            _endpoint = GetConfiguration<string>("Endpoint", string.Empty);
            _username = GetConfiguration<string>("Username", string.Empty);
            _password = GetConfiguration<string>("Password", string.Empty);

            // Validate required configuration
            if (string.IsNullOrWhiteSpace(_endpoint))
            {
                throw new InvalidOperationException("PowerScale endpoint is required. Set 'Endpoint' in configuration.");
            }
            if (string.IsNullOrWhiteSpace(_username) || string.IsNullOrWhiteSpace(_password))
            {
                throw new InvalidOperationException("PowerScale credentials are required. Set 'Username' and 'Password' in configuration.");
            }

            // Load optional configuration
            _accessZone = GetConfiguration<string>("AccessZone", "System");
            _namespace = GetConfiguration<string>("Namespace", "/ifs");
            _storagePool = GetConfiguration<string>("StoragePool", "default");
            _useSmartConnect = GetConfiguration<bool>("UseSmartConnect", false);
            _enableSnapshotIq = GetConfiguration<bool>("EnableSnapshotIQ", false);
            _enableSyncIq = GetConfiguration<bool>("EnableSyncIQ", false);
            _enableCloudPools = GetConfiguration<bool>("EnableCloudPools", false);
            _enableSmartLock = GetConfiguration<bool>("EnableSmartLock", false);
            _enableSmartDedupe = GetConfiguration<bool>("EnableSmartDedupe", false);
            _enableSmartQuotas = GetConfiguration<bool>("EnableSmartQuotas", false);
            _timeoutSeconds = GetConfiguration<int>("TimeoutSeconds", 120);
            _maxRetries = GetConfiguration<int>("MaxRetries", 3);
            _retryDelayMs = GetConfiguration<int>("RetryDelayMs", 1000);

            // Normalize endpoint
            if (!_endpoint.StartsWith("http://") && !_endpoint.StartsWith("https://"))
            {
                _endpoint = $"https://{_endpoint}";
            }
            _endpoint = _endpoint.TrimEnd('/');

            // Create HTTP client
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_timeoutSeconds)
            };

            // Authenticate and obtain session token
            await AuthenticateAsync(ct);
        }

        #region Authentication

        /// <summary>
        /// Authenticates with PowerScale OneFS PAPI and obtains a session token.
        /// </summary>
        private async Task AuthenticateAsync(CancellationToken ct)
        {
            await _authLock.WaitAsync(ct);
            try
            {
                // Check if existing token is still valid
                if (_authToken != null && DateTime.UtcNow < _authTokenExpiry)
                {
                    return;
                }

                // Create authentication request
                var authUrl = $"{_endpoint}/session/1/session";
                var request = new HttpRequestMessage(HttpMethod.Post, authUrl);

                // Use Basic Authentication for initial token acquisition
                var authBytes = Encoding.UTF8.GetBytes($"{_username}:{_password}");
                var authHeader = Convert.ToBase64String(authBytes);
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

                // Send authentication request
                var response = await _httpClient!.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                // Parse response to extract session token
                var content = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(content);

                if (doc.RootElement.TryGetProperty("id", out var idElement))
                {
                    _authToken = idElement.GetString();
                    // Token typically valid for 4 hours, refresh after 3.5 hours
                    _authTokenExpiry = DateTime.UtcNow.AddHours(3.5);
                }
                else
                {
                    throw new InvalidOperationException("Failed to obtain session token from PowerScale API");
                }
            }
            finally
            {
                _authLock.Release();
            }
        }

        /// <summary>
        /// Ensures authentication token is valid, refreshing if necessary.
        /// </summary>
        private async Task EnsureAuthenticatedAsync(CancellationToken ct)
        {
            if (_authToken == null || DateTime.UtcNow >= _authTokenExpiry)
            {
                await AuthenticateAsync(ct);
            }
        }

        #endregion

        #region Core Storage Operations

        /// <summary>
        /// Stores data to PowerScale using OneFS namespace API.
        /// </summary>
        protected override async Task<StorageObjectMetadata> StoreAsyncCore(
            string key,
            Stream data,
            IDictionary<string, string>? metadata,
            CancellationToken ct)
        {
            ValidateKey(key);
            ValidateStream(data);

            await EnsureAuthenticatedAsync(ct);

            var fullPath = BuildNamespacePath(key);
            var url = $"{_endpoint}/namespace{fullPath}";

            // Create or overwrite file
            var request = new HttpRequestMessage(HttpMethod.Put, url);
            request.Headers.Add("X-Isi-Ifs-Target-Type", "object");
            request.Headers.Add("X-Isi-Ifs-Access-Control", "public-read-write");

            // Add session token
            request.Headers.Add("Cookie", $"isisessid={_authToken}");

            // Add SmartPools policy if specified
            if (!string.IsNullOrEmpty(_storagePool) && _storagePool != "default")
            {
                request.Headers.Add("X-Isi-Ifs-Set-Pool", _storagePool);
            }

            // Add SmartLock if enabled
            if (_enableSmartLock)
            {
                request.Headers.Add("X-Isi-Ifs-Set-Worm", "true");
            }

            // Add custom metadata as extended attributes
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    request.Headers.Add($"X-Isi-Ifs-Set-Attr-{kvp.Key}", kvp.Value);
                }
            }

            // Copy stream to request content
            var ms = new MemoryStream(65536);
            await data.CopyToAsync(ms, 81920, ct);
            var content = ms.ToArray();
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            // Send request with retry
            var response = await SendWithRetryAsync(request, ct);

            // Create snapshot if SnapshotIQ is enabled
            if (_enableSnapshotIq)
            {
                await CreateSnapshotAsync(key, ct);
            }

            // Update statistics
            IncrementBytesStored(content.Length);
            IncrementOperationCounter(StorageOperationType.Store);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = content.Length,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ETag = GenerateETag(content),
                ContentType = "application/octet-stream",
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        /// <summary>
        /// Retrieves data from PowerScale using OneFS namespace API.
        /// </summary>
        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            await EnsureAuthenticatedAsync(ct);

            var fullPath = BuildNamespacePath(key);
            var url = $"{_endpoint}/namespace{fullPath}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"isisessid={_authToken}");

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
        /// Deletes an object from PowerScale.
        /// </summary>
        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            await EnsureAuthenticatedAsync(ct);

            // Get size before deletion for statistics
            long size = 0;
            try
            {
                var metadataResult = await GetMetadataAsyncCore(key, ct);
                size = metadataResult.Size;
            }
            catch
            {
                // Ignore if metadata retrieval fails
            }

            var fullPath = BuildNamespacePath(key);
            var url = $"{_endpoint}/namespace{fullPath}";

            var request = new HttpRequestMessage(HttpMethod.Delete, url);
            request.Headers.Add("Cookie", $"isisessid={_authToken}");

            await SendWithRetryAsync(request, ct);

            // Update statistics
            if (size > 0)
            {
                IncrementBytesDeleted(size);
            }
            IncrementOperationCounter(StorageOperationType.Delete);
        }

        /// <summary>
        /// Checks if an object exists in PowerScale.
        /// </summary>
        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            try
            {
                await EnsureAuthenticatedAsync(ct);

                var fullPath = BuildNamespacePath(key);
                var url = $"{_endpoint}/namespace{fullPath}";

                var request = new HttpRequestMessage(HttpMethod.Head, url);
                request.Headers.Add("Cookie", $"isisessid={_authToken}");

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
        /// Lists objects in PowerScale namespace with optional prefix filter.
        /// </summary>
        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(
            string? prefix,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await EnsureAuthenticatedAsync(ct);

            IncrementOperationCounter(StorageOperationType.List);

            var directoryPath = BuildNamespacePath(prefix ?? string.Empty);
            var url = $"{_endpoint}/namespace{directoryPath}?detail=default&max=1000";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"isisessid={_authToken}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await SendWithRetryAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(content);

            if (doc.RootElement.TryGetProperty("children", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();

                    if (!child.TryGetProperty("name", out var nameElement))
                        continue;

                    var name = nameElement.GetString();
                    if (string.IsNullOrEmpty(name))
                        continue;

                    var type = child.TryGetProperty("type", out var typeElement)
                        ? typeElement.GetString()
                        : "file";

                    // Skip directories
                    if (type != "file" && type != "object")
                        continue;

                    var size = child.TryGetProperty("size", out var sizeElement)
                        ? sizeElement.GetInt64()
                        : 0L;

                    var modified = child.TryGetProperty("mtime", out var mtimeElement)
                        ? DateTimeOffset.FromUnixTimeSeconds(mtimeElement.GetInt64()).UtcDateTime
                        : DateTime.UtcNow;

                    var fullKey = string.IsNullOrEmpty(prefix)
                        ? name
                        : $"{prefix.TrimEnd('/')}/{name}";

                    yield return new StorageObjectMetadata
                    {
                        Key = fullKey,
                        Size = size,
                        Created = modified,
                        Modified = modified,
                        ContentType = "application/octet-stream",
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

            await EnsureAuthenticatedAsync(ct);

            var fullPath = BuildNamespacePath(key);
            var url = $"{_endpoint}/namespace{fullPath}?metadata";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"isisessid={_authToken}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await SendWithRetryAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(content);
            var attrs = doc.RootElement.TryGetProperty("attrs", out var attrsElement)
                ? attrsElement
                : doc.RootElement;

            var size = attrs.TryGetProperty("size", out var sizeElement)
                ? sizeElement.GetInt64()
                : 0L;

            var modified = attrs.TryGetProperty("mtime", out var mtimeElement)
                ? DateTimeOffset.FromUnixTimeSeconds(mtimeElement.GetInt64()).UtcDateTime
                : DateTime.UtcNow;

            var created = attrs.TryGetProperty("ctime", out var ctimeElement)
                ? DateTimeOffset.FromUnixTimeSeconds(ctimeElement.GetInt64()).UtcDateTime
                : modified;

            // Extract custom metadata from extended attributes
            var customMetadata = new Dictionary<string, string>();
            if (response.Headers.TryGetValues("X-Isi-Ifs-Attr", out var attrValues))
            {
                foreach (var attr in attrValues)
                {
                    var parts = attr.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        customMetadata[parts[0]] = parts[1];
                    }
                }
            }

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = size,
                Created = created,
                Modified = modified,
                ContentType = "application/octet-stream",
                CustomMetadata = customMetadata.Count > 0 ? customMetadata : null,
                Tier = Tier
            };
        }

        /// <summary>
        /// Gets the current health status of the PowerScale cluster.
        /// </summary>
        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                await EnsureAuthenticatedAsync(ct);

                // Query cluster statistics endpoint
                var url = $"{_endpoint}/platform/1/cluster/statfs";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Cookie", $"isisessid={_authToken}");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient!.SendAsync(request, ct);
                sw.Stop();

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(content);

                    long? totalCapacity = null;
                    long? usedCapacity = null;
                    long? availableCapacity = null;

                    if (doc.RootElement.TryGetProperty("statfs", out var statfsElement))
                    {
                        if (statfsElement.TryGetProperty("f_blocks", out var blocksElement) &&
                            statfsElement.TryGetProperty("f_bsize", out var bsizeElement))
                        {
                            var blocks = blocksElement.GetInt64();
                            var blockSize = bsizeElement.GetInt64();
                            totalCapacity = blocks * blockSize;
                        }

                        if (statfsElement.TryGetProperty("f_bavail", out var bavailElement) &&
                            statfsElement.TryGetProperty("f_bsize", out var bsize2Element))
                        {
                            var availBlocks = bavailElement.GetInt64();
                            var blockSize = bsize2Element.GetInt64();
                            availableCapacity = availBlocks * blockSize;
                        }

                        if (totalCapacity.HasValue && availableCapacity.HasValue)
                        {
                            usedCapacity = totalCapacity.Value - availableCapacity.Value;
                        }
                    }

                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Healthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        TotalCapacity = totalCapacity,
                        UsedCapacity = usedCapacity,
                        AvailableCapacity = availableCapacity,
                        Message = "PowerScale cluster is healthy",
                        CheckedAt = DateTime.UtcNow
                    };
                }
                else
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Unhealthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"PowerScale cluster returned status code {response.StatusCode}",
                        CheckedAt = DateTime.UtcNow
                    };
                }
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to access PowerScale cluster: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        /// <summary>
        /// Gets the available capacity in bytes from the PowerScale cluster.
        /// </summary>
        protected override async Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            try
            {
                var health = await GetHealthAsyncCore(ct);
                return health.AvailableCapacity;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region PowerScale-Specific Operations

        /// <summary>
        /// Creates a snapshot of the specified path using SnapshotIQ.
        /// </summary>
        private async Task CreateSnapshotAsync(string key, CancellationToken ct)
        {
            try
            {
                await EnsureAuthenticatedAsync(ct);

                var fullPath = BuildNamespacePath(key);
                var snapshotName = $"snapshot-{key.Replace('/', '-')}-{DateTime.UtcNow:yyyyMMddHHmmss}";

                var url = $"{_endpoint}/platform/1/snapshot/snapshots";
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("Cookie", $"isisessid={_authToken}");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var payload = new
                {
                    name = snapshotName,
                    path = fullPath
                };

                var json = JsonSerializer.Serialize(payload);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                await _httpClient!.SendAsync(request, ct);
            }
            catch
            {
                // Snapshot creation is best-effort, don't fail the main operation
            }
        }

        /// <summary>
        /// Configures SmartQuotas for a specific directory path.
        /// </summary>
        public async Task SetQuotaAsync(string path, long hardLimitBytes, long? softLimitBytes = null, CancellationToken ct = default)
        {
            await EnsureAuthenticatedAsync(ct);

            var fullPath = BuildNamespacePath(path);
            var url = $"{_endpoint}/platform/1/quota/quotas";

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Cookie", $"isisessid={_authToken}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var payload = new
            {
                path = fullPath,
                type = "directory",
                enforced = true,
                thresholds = new
                {
                    hard = hardLimitBytes,
                    soft = softLimitBytes,
                    advisory = softLimitBytes
                }
            };

            var json = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            await SendWithRetryAsync(request, ct);
        }

        /// <summary>
        /// Initiates SyncIQ replication policy for disaster recovery.
        /// </summary>
        public async Task CreateSyncIqPolicyAsync(
            string policyName,
            string sourcePath,
            string targetCluster,
            string targetPath,
            CancellationToken ct = default)
        {
            await EnsureAuthenticatedAsync(ct);

            var url = $"{_endpoint}/platform/3/sync/policies";

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Cookie", $"isisessid={_authToken}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var payload = new
            {
                name = policyName,
                action = "sync",
                source_root_path = BuildNamespacePath(sourcePath),
                target_host = targetCluster,
                target_path = targetPath,
                enabled = true
            };

            var json = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            await SendWithRetryAsync(request, ct);
        }

        /// <summary>
        /// Configures CloudPools policy for automated cloud tiering.
        /// </summary>
        public async Task CreateCloudPoolPolicyAsync(
            string policyName,
            string cloudProvider,
            string accountName,
            CancellationToken ct = default)
        {
            await EnsureAuthenticatedAsync(ct);

            var url = $"{_endpoint}/platform/3/cloudpools/pools";

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Cookie", $"isisessid={_authToken}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var payload = new
            {
                name = policyName,
                type = cloudProvider.ToLowerInvariant(), // aws, azure, google
                account_name = accountName
            };

            var json = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            await SendWithRetryAsync(request, ct);
        }

        /// <summary>
        /// Enables SmartDedupe on a specified path for storage efficiency.
        /// </summary>
        public async Task EnableDedupeAsync(string path, CancellationToken ct = default)
        {
            await EnsureAuthenticatedAsync(ct);

            var fullPath = BuildNamespacePath(path);
            var url = $"{_endpoint}/platform/1/dedupe/settings";

            var request = new HttpRequestMessage(HttpMethod.Put, url);
            request.Headers.Add("Cookie", $"isisessid={_authToken}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var payload = new
            {
                paths = new[] { fullPath },
                dedupe_enabled = true
            };

            var json = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            await SendWithRetryAsync(request, ct);
        }

        /// <summary>
        /// Gets cluster node information for SmartConnect load balancing insights.
        /// </summary>
        public async Task<IReadOnlyList<ClusterNode>> GetClusterNodesAsync(CancellationToken ct = default)
        {
            await EnsureAuthenticatedAsync(ct);

            var url = $"{_endpoint}/platform/3/cluster/nodes";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"isisessid={_authToken}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await SendWithRetryAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(content);
            var nodes = new List<ClusterNode>();

            if (doc.RootElement.TryGetProperty("nodes", out var nodesElement))
            {
                foreach (var nodeElement in nodesElement.EnumerateArray())
                {
                    var node = new ClusterNode
                    {
                        Id = nodeElement.TryGetProperty("id", out var idElement)
                            ? idElement.GetInt32()
                            : 0,
                        Lnn = nodeElement.TryGetProperty("lnn", out var lnnElement)
                            ? lnnElement.GetInt32()
                            : 0,
                        Status = nodeElement.TryGetProperty("status", out var statusElement)
                            ? statusElement.GetString() ?? "unknown"
                            : "unknown"
                    };
                    nodes.Add(node);
                }
            }

            return nodes;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Builds the full namespace path for a given key.
        /// </summary>
        private string BuildNamespacePath(string key)
        {
            var cleanKey = key.TrimStart('/');
            var cleanNamespace = _namespace.TrimEnd('/');
            return $"{cleanNamespace}/{cleanKey}";
        }

        /// <summary>
        /// Sends an HTTP request with retry logic for transient failures.
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
                catch (Exception ex) when (attempt < _maxRetries)
                {
                    lastException = ex;
                }

                // Exponential backoff
                var delay = _retryDelayMs * (int)Math.Pow(2, attempt);
                await Task.Delay(delay, ct);

                // Recreate request for retry (can't reuse)
                request = await CloneRequestAsync(request, ct);
            }

            if (lastException != null)
            {
                throw lastException;
            }

            response?.EnsureSuccessStatusCode();
            return response!;
        }

        /// <summary>
        /// Clones an HTTP request for retry attempts.
        /// </summary>
        private async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);

            // Clone headers
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            // Clone content if present
            if (request.Content != null)
            {
                var content = await request.Content.ReadAsByteArrayAsync(ct);
                clone.Content = new ByteArrayContent(content);

                // Clone content headers
                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }

        /// <summary>
        /// Determines if an HTTP status code should trigger a retry.
        /// </summary>
        private bool ShouldRetry(System.Net.HttpStatusCode statusCode)
        {
            return statusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                   statusCode == System.Net.HttpStatusCode.RequestTimeout ||
                   statusCode == System.Net.HttpStatusCode.TooManyRequests ||
                   statusCode == System.Net.HttpStatusCode.GatewayTimeout ||
                   (int)statusCode >= 500;
        }

        /// <summary>
        /// Generates an ETag from content bytes.
        /// </summary>
        private string GenerateETag(byte[] content)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(content);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Gets the maximum allowed key length for PowerScale.
        /// </summary>
        protected override int GetMaxKeyLength() => 4096;

        #endregion

        #region Cleanup

        /// <summary>
        /// Disposes resources used by this storage strategy.
        /// </summary>
        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();

            // Logout and invalidate session token
            if (_httpClient != null && _authToken != null)
            {
                try
                {
                    var url = $"{_endpoint}/session/1/session";
                    var request = new HttpRequestMessage(HttpMethod.Delete, url);
                    request.Headers.Add("Cookie", $"isisessid={_authToken}");
                    await _httpClient.SendAsync(request);
                }
                catch
                {
                    // Ignore logout errors during disposal
                }
            }

            _httpClient?.Dispose();
            _authLock?.Dispose();
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Represents a node in the PowerScale cluster.
    /// </summary>
    public class ClusterNode
    {
        /// <summary>
        /// Node identifier.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Logical Node Number (LNN).
        /// </summary>
        public int Lnn { get; set; }

        /// <summary>
        /// Node status (online, offline, degraded).
        /// </summary>
        public string Status { get; set; } = string.Empty;
    }

    #endregion
}
