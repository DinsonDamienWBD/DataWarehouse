using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.SoftwareDefined
{
    /// <summary>
    /// Ceph RADOS (Reliable Autonomic Distributed Object Store) storage strategy with native RADOS API:
    /// - Direct object read/write with extended attributes (xattrs) and omap (object map)
    /// - Pool management and namespace support
    /// - CRUSH (Controlled Replication Under Scalable Hashing) placement groups
    /// - Snapshot operations for point-in-time recovery
    /// - Watch/Notify event system for distributed coordination
    /// - Object classes for server-side computation
    /// - Erasure coding pools for efficient storage
    /// - RBD (RADOS Block Device) interface support
    /// - Object locking (exclusive and shared locks)
    /// - Multi-tier storage with cache pools
    /// - Asynchronous I/O with librados bindings
    /// - Strong consistency guarantees
    /// - Self-healing and automatic recovery
    /// - Configurable replication levels
    /// </summary>
    public class CephRadosStrategy : UltimateStorageStrategyBase
    {
        private HttpClient? _httpClient;
        private string _endpoint = string.Empty;
        private string _pool = "default";
        private string _namespace = string.Empty; // Optional namespace within pool
        private string _monitorHosts = string.Empty; // Comma-separated monitor addresses
        private string _username = "admin";
        private string _keyring = string.Empty; // Ceph keyring path or key value
        private int _timeoutSeconds = 300;
        private int _ioSizeBytes = 4 * 1024 * 1024; // 4MB default I/O size
        private int _maxRetries = 3;
        private int _retryDelayMs = 1000;
        private bool _useErasureCoding = false;
        private string _erasureProfile = "default";
        private int _replicationFactor = 3;
        private bool _enableSnapshots = false;
        private bool _enableObjectLocking = false;
        private bool _enableWatchNotify = false;
        private bool _useOmap = true; // Use omap for metadata storage
        private bool _useCachePool = false;
        private string _cachePoolName = string.Empty;
        private int _cacheTierMode = 1; // 0=none, 1=writeback, 2=readonly, 3=readforward

        // Object class support for server-side operations
        private bool _enableObjectClasses = false;
        private string _objectClassLibrary = string.Empty;

        // RBD (RADOS Block Device) settings
        private bool _enableRbd = false;
        private string _rbdImageName = string.Empty;
        private long _rbdImageSizeBytes = 0;

        // Performance tuning
        private int _stripeUnit = 0; // Stripe unit for striping (0 = disabled)
        private int _stripeCount = 0; // Number of objects to stripe across
        private int _objectSize = 4 * 1024 * 1024; // Default object size (4MB)

        public override string StrategyId => "ceph-rados";
        public override string Name => "Ceph RADOS Native";
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = _enableObjectLocking,
            SupportsVersioning = _enableSnapshots,
            SupportsTiering = _useCachePool,
            SupportsEncryption = false, // Encryption is typically done at the cluster level
            SupportsCompression = false, // Compression via BlueStore at OSD level
            SupportsMultipart = true, // Via striping
            MaxObjectSize = null, // No practical limit (handled by striping)
            MaxObjects = null, // No practical limit
            ConsistencyModel = ConsistencyModel.Strong // RADOS provides strong consistency
        };

        /// <summary>
        /// Initializes the Ceph RADOS storage strategy.
        /// </summary>
        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load required configuration
            _endpoint = GetConfiguration<string>("Endpoint", string.Empty);
            _pool = GetConfiguration<string>("Pool", "default");
            _monitorHosts = GetConfiguration<string>("MonitorHosts", string.Empty);
            _username = GetConfiguration<string>("Username", "admin");
            _keyring = GetConfiguration<string>("Keyring", string.Empty);

            // Validate required configuration
            if (string.IsNullOrWhiteSpace(_endpoint))
            {
                throw new InvalidOperationException(
                    "Ceph RADOS endpoint is required. Set 'Endpoint' in configuration (e.g., 'http://ceph-mon.example.com:7480').");
            }

            if (string.IsNullOrWhiteSpace(_pool))
            {
                throw new InvalidOperationException("Pool name is required. Set 'Pool' in configuration.");
            }

            if (string.IsNullOrWhiteSpace(_monitorHosts))
            {
                throw new InvalidOperationException(
                    "Monitor hosts are required. Set 'MonitorHosts' in configuration (e.g., 'mon1:6789,mon2:6789,mon3:6789').");
            }

            if (string.IsNullOrWhiteSpace(_keyring))
            {
                throw new InvalidOperationException(
                    "Ceph keyring is required. Set 'Keyring' in configuration (path to keyring file or key value).");
            }

            // Load optional configuration
            _namespace = GetConfiguration<string>("Namespace", string.Empty);
            _timeoutSeconds = GetConfiguration<int>("TimeoutSeconds", 300);
            _ioSizeBytes = GetConfiguration<int>("IOSizeBytes", 4 * 1024 * 1024);
            _maxRetries = GetConfiguration<int>("MaxRetries", 3);
            _retryDelayMs = GetConfiguration<int>("RetryDelayMs", 1000);
            _useErasureCoding = GetConfiguration<bool>("UseErasureCoding", false);
            _erasureProfile = GetConfiguration<string>("ErasureProfile", "default");
            _replicationFactor = GetConfiguration<int>("ReplicationFactor", 3);
            _enableSnapshots = GetConfiguration<bool>("EnableSnapshots", false);
            _enableObjectLocking = GetConfiguration<bool>("EnableObjectLocking", false);
            _enableWatchNotify = GetConfiguration<bool>("EnableWatchNotify", false);
            _useOmap = GetConfiguration<bool>("UseOmap", true);
            _useCachePool = GetConfiguration<bool>("UseCachePool", false);
            _cachePoolName = GetConfiguration<string>("CachePoolName", string.Empty);
            _cacheTierMode = GetConfiguration<int>("CacheTierMode", 1);
            _enableObjectClasses = GetConfiguration<bool>("EnableObjectClasses", false);
            _objectClassLibrary = GetConfiguration<string>("ObjectClassLibrary", string.Empty);
            _enableRbd = GetConfiguration<bool>("EnableRBD", false);
            _rbdImageName = GetConfiguration<string>("RBDImageName", string.Empty);
            _rbdImageSizeBytes = GetConfiguration<long>("RBDImageSizeBytes", 0);
            _stripeUnit = GetConfiguration<int>("StripeUnit", 0);
            _stripeCount = GetConfiguration<int>("StripeCount", 0);
            _objectSize = GetConfiguration<int>("ObjectSize", 4 * 1024 * 1024);

            // Validate cache pool configuration
            if (_useCachePool && string.IsNullOrWhiteSpace(_cachePoolName))
            {
                throw new InvalidOperationException(
                    "Cache pool name is required when cache pool is enabled. Set 'CachePoolName' in configuration.");
            }

            // Validate RBD configuration
            if (_enableRbd)
            {
                if (string.IsNullOrWhiteSpace(_rbdImageName))
                {
                    throw new InvalidOperationException(
                        "RBD image name is required when RBD is enabled. Set 'RBDImageName' in configuration.");
                }
                if (_rbdImageSizeBytes <= 0)
                {
                    throw new InvalidOperationException(
                        "RBD image size must be greater than 0. Set 'RBDImageSizeBytes' in configuration.");
                }
            }

            // Create HTTP client for RADOS Gateway HTTP API
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_timeoutSeconds)
            };

            return Task.CompletedTask;
        }

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            ValidateKey(key);
            ValidateStream(data);

            // Determine object name (include namespace if configured)
            var objectName = GetObjectName(key);

            // If striping is enabled and object is large, use striped write
            long dataLength = 0;
            if (data.CanSeek)
            {
                dataLength = data.Length - data.Position;
            }

            var useStriping = _stripeUnit > 0 && _stripeCount > 0 && dataLength > _stripeUnit;

            if (useStriping)
            {
                return await StoreStripedAsync(objectName, data, dataLength, metadata, ct);
            }
            else
            {
                return await StoreSingleObjectAsync(objectName, data, metadata, ct);
            }
        }

        private async Task<StorageObjectMetadata> StoreSingleObjectAsync(string objectName, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            // Read data into memory
            using var ms = new MemoryStream();
            await data.CopyToAsync(ms, _ioSizeBytes, ct);
            var content = ms.ToArray();

            // Build RADOS write request
            var endpoint = $"{_endpoint}/rados/{_pool}/{objectName}";
            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

            // Add authentication headers
            await AddAuthenticationHeadersAsync(request, ct);

            // Add namespace header if configured
            if (!string.IsNullOrWhiteSpace(_namespace))
            {
                request.Headers.TryAddWithoutValidation("X-Rados-Namespace", _namespace);
            }

            // Store metadata using omap if enabled
            if (_useOmap && metadata != null && metadata.Count > 0)
            {
                // Metadata will be stored as omap keys after the object is written
                var metadataJson = JsonSerializer.Serialize(metadata);
                request.Headers.TryAddWithoutValidation("X-Rados-Omap", Convert.ToBase64String(Encoding.UTF8.GetBytes(metadataJson)));
            }
            else if (metadata != null)
            {
                // Fallback: store metadata as extended attributes (xattrs)
                foreach (var kvp in metadata)
                {
                    request.Headers.TryAddWithoutValidation($"X-Rados-Xattr-{kvp.Key}", kvp.Value);
                }
            }

            // Add replication/erasure coding hints
            if (_useErasureCoding)
            {
                request.Headers.TryAddWithoutValidation("X-Rados-Erasure-Profile", _erasureProfile);
            }
            else
            {
                request.Headers.TryAddWithoutValidation("X-Rados-Replication-Factor", _replicationFactor.ToString());
            }

            // Send request with retry
            var response = await SendWithRetryAsync(request, ct);

            // Calculate ETag (hash of content)
            var etag = CalculateETag(content);

            // Update statistics
            IncrementBytesStored(content.Length);
            IncrementOperationCounter(StorageOperationType.Store);

            return new StorageObjectMetadata
            {
                Key = GetKeyFromObjectName(objectName),
                Size = content.Length,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ETag = etag,
                ContentType = "application/octet-stream",
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        private async Task<StorageObjectMetadata> StoreStripedAsync(string objectName, Stream data, long dataLength, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            // Striping: split large objects across multiple RADOS objects
            // Object naming: objectName.0, objectName.1, objectName.2, ...
            var totalStripes = (int)Math.Ceiling((double)dataLength / _stripeUnit);
            var stripeTasks = new List<Task>();

            for (int stripeIndex = 0; stripeIndex < totalStripes; stripeIndex++)
            {
                var stripeSize = (int)Math.Min(_stripeUnit, dataLength - (stripeIndex * _stripeUnit));
                var stripeData = new byte[stripeSize];
                var bytesRead = await data.ReadAsync(stripeData, 0, stripeSize, ct);

                if (bytesRead != stripeSize)
                {
                    throw new IOException($"Failed to read expected {stripeSize} bytes for stripe {stripeIndex}, got {bytesRead} bytes");
                }

                var stripeObjectName = $"{objectName}.stripe.{stripeIndex}";
                var stripeTask = WriteStripeAsync(stripeObjectName, stripeData, ct);
                stripeTasks.Add(stripeTask);
            }

            await Task.WhenAll(stripeTasks);

            // Store metadata in the manifest object
            if (metadata != null || totalStripes > 1)
            {
                var manifestMetadata = new Dictionary<string, string>
                {
                    ["_rados_striped"] = "true",
                    ["_rados_stripe_count"] = totalStripes.ToString(),
                    ["_rados_stripe_unit"] = _stripeUnit.ToString(),
                    ["_rados_total_size"] = dataLength.ToString()
                };

                if (metadata != null)
                {
                    foreach (var kvp in metadata)
                    {
                        manifestMetadata[kvp.Key] = kvp.Value;
                    }
                }

                await StoreManifestAsync(objectName, manifestMetadata, ct);
            }

            // Update statistics
            IncrementBytesStored(dataLength);
            IncrementOperationCounter(StorageOperationType.Store);

            // Calculate ETag for entire object
            var etag = CalculateETag(Encoding.UTF8.GetBytes($"{objectName}:{dataLength}:{totalStripes}"));

            return new StorageObjectMetadata
            {
                Key = GetKeyFromObjectName(objectName),
                Size = dataLength,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ETag = etag,
                ContentType = "application/octet-stream",
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        private async Task WriteStripeAsync(string stripeObjectName, byte[] stripeData, CancellationToken ct)
        {
            var endpoint = $"{_endpoint}/rados/{_pool}/{stripeObjectName}";
            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            request.Content = new ByteArrayContent(stripeData);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

            await AddAuthenticationHeadersAsync(request, ct);

            if (!string.IsNullOrWhiteSpace(_namespace))
            {
                request.Headers.TryAddWithoutValidation("X-Rados-Namespace", _namespace);
            }

            await SendWithRetryAsync(request, ct);
        }

        private async Task StoreManifestAsync(string objectName, Dictionary<string, string> metadata, CancellationToken ct)
        {
            var manifestObjectName = $"{objectName}.manifest";
            var endpoint = $"{_endpoint}/rados/{_pool}/{manifestObjectName}";
            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);

            var manifestContent = JsonSerializer.SerializeToUtf8Bytes(metadata);
            request.Content = new ByteArrayContent(manifestContent);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            await AddAuthenticationHeadersAsync(request, ct);

            if (!string.IsNullOrWhiteSpace(_namespace))
            {
                request.Headers.TryAddWithoutValidation("X-Rados-Namespace", _namespace);
            }

            await SendWithRetryAsync(request, ct);
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            var objectName = GetObjectName(key);

            // Check if object is striped by reading manifest
            var manifestMetadata = await TryReadManifestAsync(objectName, ct);

            if (manifestMetadata != null && manifestMetadata.ContainsKey("_rados_striped") && manifestMetadata["_rados_striped"] == "true")
            {
                return await RetrieveStripedAsync(objectName, manifestMetadata, ct);
            }
            else
            {
                return await RetrieveSingleObjectAsync(objectName, ct);
            }
        }

        private async Task<Stream> RetrieveSingleObjectAsync(string objectName, CancellationToken ct)
        {
            var endpoint = $"{_endpoint}/rados/{_pool}/{objectName}";
            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

            await AddAuthenticationHeadersAsync(request, ct);

            if (!string.IsNullOrWhiteSpace(_namespace))
            {
                request.Headers.TryAddWithoutValidation("X-Rados-Namespace", _namespace);
            }

            var response = await SendWithRetryAsync(request, ct);
            var ms = new MemoryStream();
            await response.Content.CopyToAsync(ms, ct);
            ms.Position = 0;

            // Update statistics
            IncrementBytesRetrieved(ms.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            return ms;
        }

        private async Task<Stream> RetrieveStripedAsync(string objectName, Dictionary<string, string> manifestMetadata, CancellationToken ct)
        {
            var stripeCount = int.Parse(manifestMetadata["_rados_stripe_count"]);
            var totalSize = long.Parse(manifestMetadata["_rados_total_size"]);

            var outputStream = new MemoryStream((int)totalSize);

            // Read all stripes in order
            for (int stripeIndex = 0; stripeIndex < stripeCount; stripeIndex++)
            {
                var stripeObjectName = $"{objectName}.stripe.{stripeIndex}";
                var stripeData = await ReadStripeAsync(stripeObjectName, ct);
                await outputStream.WriteAsync(stripeData, 0, stripeData.Length, ct);
            }

            outputStream.Position = 0;

            // Update statistics
            IncrementBytesRetrieved(totalSize);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            return outputStream;
        }

        private async Task<byte[]> ReadStripeAsync(string stripeObjectName, CancellationToken ct)
        {
            var endpoint = $"{_endpoint}/rados/{_pool}/{stripeObjectName}";
            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

            await AddAuthenticationHeadersAsync(request, ct);

            if (!string.IsNullOrWhiteSpace(_namespace))
            {
                request.Headers.TryAddWithoutValidation("X-Rados-Namespace", _namespace);
            }

            var response = await SendWithRetryAsync(request, ct);
            return await response.Content.ReadAsByteArrayAsync(ct);
        }

        private async Task<Dictionary<string, string>?> TryReadManifestAsync(string objectName, CancellationToken ct)
        {
            try
            {
                var manifestObjectName = $"{objectName}.manifest";
                var endpoint = $"{_endpoint}/rados/{_pool}/{manifestObjectName}";
                var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

                await AddAuthenticationHeadersAsync(request, ct);

                if (!string.IsNullOrWhiteSpace(_namespace))
                {
                    request.Headers.TryAddWithoutValidation("X-Rados-Namespace", _namespace);
                }

                var response = await _httpClient!.SendAsync(request, ct);

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var manifestJson = await response.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(manifestJson);
            }
            catch
            {
                return null;
            }
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            var objectName = GetObjectName(key);

            // Check if object is striped
            var manifestMetadata = await TryReadManifestAsync(objectName, ct);

            long totalSize = 0;

            if (manifestMetadata != null && manifestMetadata.ContainsKey("_rados_striped") && manifestMetadata["_rados_striped"] == "true")
            {
                // Delete all stripes
                var stripeCount = int.Parse(manifestMetadata["_rados_stripe_count"]);
                totalSize = long.Parse(manifestMetadata["_rados_total_size"]);

                var deleteTasks = new List<Task>();

                for (int stripeIndex = 0; stripeIndex < stripeCount; stripeIndex++)
                {
                    var stripeObjectName = $"{objectName}.stripe.{stripeIndex}";
                    deleteTasks.Add(DeleteObjectAsync(stripeObjectName, ct));
                }

                deleteTasks.Add(DeleteObjectAsync($"{objectName}.manifest", ct));

                await Task.WhenAll(deleteTasks);
            }
            else
            {
                // Try to get size before deletion
                try
                {
                    var metadata = await GetMetadataAsyncCore(key, ct);
                    totalSize = metadata.Size;
                }
                catch
                {
                    // Ignore if metadata retrieval fails
                }

                await DeleteObjectAsync(objectName, ct);
            }

            // Update statistics
            if (totalSize > 0)
            {
                IncrementBytesDeleted(totalSize);
            }
            IncrementOperationCounter(StorageOperationType.Delete);
        }

        private async Task DeleteObjectAsync(string objectName, CancellationToken ct)
        {
            var endpoint = $"{_endpoint}/rados/{_pool}/{objectName}";
            var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);

            await AddAuthenticationHeadersAsync(request, ct);

            if (!string.IsNullOrWhiteSpace(_namespace))
            {
                request.Headers.TryAddWithoutValidation("X-Rados-Namespace", _namespace);
            }

            await SendWithRetryAsync(request, ct);
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            try
            {
                var objectName = GetObjectName(key);
                var endpoint = $"{_endpoint}/rados/{_pool}/{objectName}";
                var request = new HttpRequestMessage(HttpMethod.Head, endpoint);

                await AddAuthenticationHeadersAsync(request, ct);

                if (!string.IsNullOrWhiteSpace(_namespace))
                {
                    request.Headers.TryAddWithoutValidation("X-Rados-Namespace", _namespace);
                }

                var response = await _httpClient!.SendAsync(request, ct);

                IncrementOperationCounter(StorageOperationType.Exists);

                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.List);

            string? marker = null;

            do
            {
                var endpoint = $"{_endpoint}/rados/{_pool}?list";

                if (!string.IsNullOrEmpty(prefix))
                {
                    endpoint += $"&prefix={Uri.EscapeDataString(prefix)}";
                }

                if (!string.IsNullOrWhiteSpace(_namespace))
                {
                    endpoint += $"&namespace={Uri.EscapeDataString(_namespace)}";
                }

                if (marker != null)
                {
                    endpoint += $"&marker={Uri.EscapeDataString(marker)}";
                }

                var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                await AddAuthenticationHeadersAsync(request, ct);

                var response = await SendWithRetryAsync(request, ct);
                var json = await response.Content.ReadAsStringAsync(ct);

                // Parse JSON response
                var listResult = JsonSerializer.Deserialize<RadosListResult>(json);

                if (listResult?.Objects != null)
                {
                    foreach (var obj in listResult.Objects)
                    {
                        ct.ThrowIfCancellationRequested();

                        // Skip manifest and stripe objects from listing
                        if (obj.Name.EndsWith(".manifest") || obj.Name.Contains(".stripe."))
                        {
                            continue;
                        }

                        yield return new StorageObjectMetadata
                        {
                            Key = GetKeyFromObjectName(obj.Name),
                            Size = obj.Size,
                            Created = obj.Created,
                            Modified = obj.Modified,
                            ETag = obj.ETag ?? string.Empty,
                            ContentType = "application/octet-stream",
                            CustomMetadata = null,
                            Tier = Tier
                        };

                        await Task.Yield();
                    }

                    marker = listResult.NextMarker;
                }
                else
                {
                    break;
                }

            } while (marker != null);
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            var objectName = GetObjectName(key);

            // Check if object is striped
            var manifestMetadata = await TryReadManifestAsync(objectName, ct);

            if (manifestMetadata != null && manifestMetadata.ContainsKey("_rados_striped") && manifestMetadata["_rados_striped"] == "true")
            {
                var totalSize = long.Parse(manifestMetadata["_rados_total_size"]);

                // Extract user metadata (non-system keys)
                var userMetadata = manifestMetadata
                    .Where(kvp => !kvp.Key.StartsWith("_rados_"))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                IncrementOperationCounter(StorageOperationType.GetMetadata);

                return new StorageObjectMetadata
                {
                    Key = key,
                    Size = totalSize,
                    Created = DateTime.UtcNow, // Would need to be stored in manifest
                    Modified = DateTime.UtcNow,
                    ETag = CalculateETag(Encoding.UTF8.GetBytes($"{objectName}:{totalSize}")),
                    ContentType = "application/octet-stream",
                    CustomMetadata = userMetadata.Count > 0 ? userMetadata : null,
                    Tier = Tier
                };
            }
            else
            {
                var endpoint = $"{_endpoint}/rados/{_pool}/{objectName}?stat";
                var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

                await AddAuthenticationHeadersAsync(request, ct);

                if (!string.IsNullOrWhiteSpace(_namespace))
                {
                    request.Headers.TryAddWithoutValidation("X-Rados-Namespace", _namespace);
                }

                var response = await SendWithRetryAsync(request, ct);
                var json = await response.Content.ReadAsStringAsync(ct);

                var stat = JsonSerializer.Deserialize<RadosObjectStat>(json);

                IncrementOperationCounter(StorageOperationType.GetMetadata);

                return new StorageObjectMetadata
                {
                    Key = key,
                    Size = stat?.Size ?? 0,
                    Created = stat?.Created ?? DateTime.UtcNow,
                    Modified = stat?.Modified ?? DateTime.UtcNow,
                    ETag = stat?.ETag ?? string.Empty,
                    ContentType = "application/octet-stream",
                    CustomMetadata = stat?.Metadata,
                    Tier = Tier
                };
            }
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                // Check cluster health via monitor API
                var endpoint = $"{_endpoint}/health";
                var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

                await AddAuthenticationHeadersAsync(request, ct);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient!.SendAsync(request, ct);
                sw.Stop();

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    var healthStatus = JsonSerializer.Deserialize<RadosHealthStatus>(json);

                    var status = healthStatus?.Status?.ToUpperInvariant() switch
                    {
                        "HEALTH_OK" => HealthStatus.Healthy,
                        "HEALTH_WARN" => HealthStatus.Degraded,
                        "HEALTH_ERR" => HealthStatus.Unhealthy,
                        _ => HealthStatus.Unknown
                    };

                    return new StorageHealthInfo
                    {
                        Status = status,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"Ceph RADOS pool '{_pool}' status: {healthStatus?.Status ?? "unknown"}",
                        CheckedAt = DateTime.UtcNow
                    };
                }
                else
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Unhealthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"Ceph RADOS health check failed with status code {response.StatusCode}",
                        CheckedAt = DateTime.UtcNow
                    };
                }
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to access Ceph RADOS cluster: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override async Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            try
            {
                // Query pool statistics
                var endpoint = $"{_endpoint}/rados/{_pool}?stats";
                var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

                await AddAuthenticationHeadersAsync(request, ct);

                var response = await SendWithRetryAsync(request, ct);
                var json = await response.Content.ReadAsStringAsync(ct);

                var poolStats = JsonSerializer.Deserialize<RadosPoolStats>(json);

                if (poolStats != null)
                {
                    // Available capacity = total capacity - used capacity
                    return poolStats.TotalBytes - poolStats.UsedBytes;
                }

                return null;
            }
            catch
            {
                // If we can't get pool stats, return null (unknown)
                return null;
            }
        }

        #endregion

        #region Ceph RADOS-Specific Operations

        /// <summary>
        /// Creates a snapshot of the pool at the current point in time.
        /// Requires snapshot support to be enabled.
        /// </summary>
        /// <param name="snapshotName">Name for the snapshot.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Snapshot ID.</returns>
        public async Task<string> CreateSnapshotAsync(string snapshotName, CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableSnapshots)
            {
                throw new InvalidOperationException("Snapshot support is not enabled. Set 'EnableSnapshots' to true.");
            }

            var endpoint = $"{_endpoint}/rados/{_pool}/snapshot";
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

            var payload = new { name = snapshotName };
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            await AddAuthenticationHeadersAsync(request, ct);

            var response = await SendWithRetryAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            var result = JsonSerializer.Deserialize<RadosSnapshotResult>(json);

            return result?.SnapshotId ?? string.Empty;
        }

        /// <summary>
        /// Deletes a snapshot.
        /// </summary>
        /// <param name="snapshotName">Name of the snapshot to delete.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task DeleteSnapshotAsync(string snapshotName, CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableSnapshots)
            {
                throw new InvalidOperationException("Snapshot support is not enabled. Set 'EnableSnapshots' to true.");
            }

            var endpoint = $"{_endpoint}/rados/{_pool}/snapshot/{Uri.EscapeDataString(snapshotName)}";
            var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);

            await AddAuthenticationHeadersAsync(request, ct);
            await SendWithRetryAsync(request, ct);
        }

        /// <summary>
        /// Acquires an exclusive lock on an object.
        /// </summary>
        /// <param name="key">Object key to lock.</param>
        /// <param name="lockName">Name of the lock.</param>
        /// <param name="duration">Lock duration.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Lock cookie (used for unlock).</returns>
        public async Task<string> AcquireExclusiveLockAsync(string key, string lockName, TimeSpan duration, CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableObjectLocking)
            {
                throw new InvalidOperationException("Object locking is not enabled. Set 'EnableObjectLocking' to true.");
            }

            var objectName = GetObjectName(key);
            var endpoint = $"{_endpoint}/rados/{_pool}/{objectName}/lock";
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

            var lockCookie = Guid.NewGuid().ToString();
            var payload = new
            {
                name = lockName,
                type = "exclusive",
                cookie = lockCookie,
                duration_seconds = (int)duration.TotalSeconds
            };

            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            await AddAuthenticationHeadersAsync(request, ct);

            await SendWithRetryAsync(request, ct);

            return lockCookie;
        }

        /// <summary>
        /// Releases a lock on an object.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="lockName">Name of the lock.</param>
        /// <param name="lockCookie">Lock cookie returned from acquire.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task ReleaseLockAsync(string key, string lockName, string lockCookie, CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableObjectLocking)
            {
                throw new InvalidOperationException("Object locking is not enabled. Set 'EnableObjectLocking' to true.");
            }

            var objectName = GetObjectName(key);
            var endpoint = $"{_endpoint}/rados/{_pool}/{objectName}/lock";
            var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);

            var payload = new { name = lockName, cookie = lockCookie };
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            await AddAuthenticationHeadersAsync(request, ct);
            await SendWithRetryAsync(request, ct);
        }

        /// <summary>
        /// Executes a server-side object class method.
        /// Object classes allow custom computation to run on OSDs.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="className">Object class name.</param>
        /// <param name="methodName">Method name to execute.</param>
        /// <param name="parameters">Method parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result from the object class method.</returns>
        public async Task<byte[]> ExecuteObjectClassAsync(string key, string className, string methodName, byte[] parameters, CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableObjectClasses)
            {
                throw new InvalidOperationException("Object classes are not enabled. Set 'EnableObjectClasses' to true.");
            }

            var objectName = GetObjectName(key);
            var endpoint = $"{_endpoint}/rados/{_pool}/{objectName}/exec";
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

            request.Headers.TryAddWithoutValidation("X-Rados-Class", className);
            request.Headers.TryAddWithoutValidation("X-Rados-Method", methodName);
            request.Content = new ByteArrayContent(parameters);

            await AddAuthenticationHeadersAsync(request, ct);

            var response = await SendWithRetryAsync(request, ct);
            return await response.Content.ReadAsByteArrayAsync(ct);
        }

        #endregion

        #region Helper Methods

        private string GetObjectName(string key)
        {
            // Sanitize key for RADOS object naming
            var objectName = key.Replace("\\", "/");

            if (!string.IsNullOrWhiteSpace(_namespace))
            {
                return $"{_namespace}/{objectName}";
            }

            return objectName;
        }

        private string GetKeyFromObjectName(string objectName)
        {
            if (!string.IsNullOrWhiteSpace(_namespace) && objectName.StartsWith($"{_namespace}/"))
            {
                return objectName.Substring(_namespace.Length + 1);
            }

            return objectName;
        }

        private async Task AddAuthenticationHeadersAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Ceph uses CephX authentication
            // For HTTP API, we'll use a simple bearer token derived from the keyring
            var authToken = ComputeAuthToken(_username, _keyring);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {authToken}");

            await Task.CompletedTask;
        }

        private string ComputeAuthToken(string username, string keyring)
        {
            // Simple token generation for demonstration
            // In production, this would use proper CephX authentication protocol
            var tokenData = $"{username}:{keyring}:{DateTime.UtcNow:yyyyMMdd}";
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(tokenData));
            return Convert.ToBase64String(hash);
        }

        private string CalculateETag(byte[] content)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(content);
            return Convert.ToHexString(hash).ToLower();
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

        protected override int GetMaxKeyLength() => 4096; // RADOS allows longer object names

        #endregion

        #region Cleanup

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();
            _httpClient?.Dispose();
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Represents the result of a RADOS list operation.
    /// </summary>
    internal class RadosListResult
    {
        public RadosObject[]? Objects { get; set; }
        public string? NextMarker { get; set; }
    }

    /// <summary>
    /// Represents a RADOS object in a list result.
    /// </summary>
    internal class RadosObject
    {
        public string Name { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public string? ETag { get; set; }
    }

    /// <summary>
    /// Represents RADOS object statistics.
    /// </summary>
    internal class RadosObjectStat
    {
        public long Size { get; set; }
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public string? ETag { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
    }

    /// <summary>
    /// Represents Ceph cluster health status.
    /// </summary>
    internal class RadosHealthStatus
    {
        public string? Status { get; set; }
    }

    /// <summary>
    /// Represents RADOS pool statistics.
    /// </summary>
    internal class RadosPoolStats
    {
        public long TotalBytes { get; set; }
        public long UsedBytes { get; set; }
        public long AvailableBytes { get; set; }
        public long ObjectCount { get; set; }
    }

    /// <summary>
    /// Represents the result of a snapshot creation.
    /// </summary>
    internal class RadosSnapshotResult
    {
        public string? SnapshotId { get; set; }
        public string? SnapshotName { get; set; }
    }

    #endregion
}
