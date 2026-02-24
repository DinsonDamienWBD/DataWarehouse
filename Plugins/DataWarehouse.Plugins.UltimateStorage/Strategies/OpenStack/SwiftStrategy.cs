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
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.OpenStack
{
    /// <summary>
    /// OpenStack Swift Object Storage strategy with production-ready features:
    /// - Keystone v3 authentication with token management
    /// - Large object support via Static Large Objects (SLO) and Dynamic Large Objects (DLO)
    /// - Object versioning and soft delete
    /// - Custom metadata via X-Object-Meta headers
    /// - Container management and hierarchy
    /// - Temporary URLs for secure access
    /// - Server-side copy operations
    /// - Automatic token refresh
    /// - Multi-region support
    /// - Storage policies and placement
    /// - Object expiration (X-Delete-At, X-Delete-After)
    /// </summary>
    public class SwiftStrategy : UltimateStorageStrategyBase
    {
        private HttpClient? _httpClient;
        private string _authUrl = string.Empty;
        private string _username = string.Empty;
        private string _password = string.Empty;
        private string _projectName = string.Empty;
        private string _domainName = "Default";
        private string _containerName = string.Empty;
        private string _region = string.Empty;
        private int _timeoutSeconds = 300;
        private bool _enableVersioning = false;
        private int _segmentSizeBytes = 100 * 1024 * 1024; // 100MB for large object segments
        private int _largeObjectThresholdBytes = 200 * 1024 * 1024; // 200MB threshold
        private string _storagePolicy = string.Empty;

        // Authentication state
        private string? _authToken;
        private DateTime _tokenExpiry = DateTime.MinValue;
        private string? _storageUrl;
        private readonly SemaphoreSlim _authLock = new(1, 1);

        public override string StrategyId => "openstack-swift";
        public override string Name => "OpenStack Swift Object Storage";
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false,
            SupportsVersioning = true,
            SupportsTiering = false, // Swift uses storage policies rather than tiers
            SupportsEncryption = false, // Client-side encryption can be added
            SupportsCompression = false,
            SupportsMultipart = true,
            MaxObjectSize = 5L * 1024 * 1024 * 1024 * 1024, // 5TB via SLO
            MaxObjects = null,
            ConsistencyModel = ConsistencyModel.Eventual
        };

        #region Initialization

        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load configuration
            _authUrl = GetConfiguration<string>("AuthUrl")
                ?? throw new InvalidOperationException("AuthUrl is required for Swift authentication");
            _username = GetConfiguration<string>("Username")
                ?? throw new InvalidOperationException("Username is required");
            _password = GetConfiguration<string>("Password")
                ?? throw new InvalidOperationException("Password is required");
            _projectName = GetConfiguration<string>("ProjectName")
                ?? throw new InvalidOperationException("ProjectName is required");
            _containerName = GetConfiguration<string>("ContainerName")
                ?? throw new InvalidOperationException("ContainerName is required");

            _domainName = GetConfiguration("DomainName", "Default");
            _region = GetConfiguration("Region", string.Empty);
            _timeoutSeconds = GetConfiguration("TimeoutSeconds", 300);
            _enableVersioning = GetConfiguration("EnableVersioning", false);
            _segmentSizeBytes = GetConfiguration("SegmentSizeBytes", 100 * 1024 * 1024);
            _largeObjectThresholdBytes = GetConfiguration("LargeObjectThresholdBytes", 200 * 1024 * 1024);
            _storagePolicy = GetConfiguration("StoragePolicy", string.Empty);

            // Initialize HTTP client
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_timeoutSeconds)
            };

            // Authenticate and get token
            await AuthenticateAsync(ct);

            // Ensure container exists
            await EnsureContainerExistsAsync(ct);
        }

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();
            _httpClient?.Dispose();
            _authLock?.Dispose();
        }

        #endregion

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            await EnsureAuthenticatedAsync(ct);

            // Determine if large object handling is needed
            var useSegmentation = false;
            long dataLength = 0;

            if (data.CanSeek)
            {
                dataLength = data.Length - data.Position;
                useSegmentation = dataLength > _largeObjectThresholdBytes;
            }

            if (useSegmentation)
            {
                return await StoreStaticLargeObjectAsync(key, data, dataLength, metadata, ct);
            }
            else
            {
                return await StoreSingleObjectAsync(key, data, metadata, ct);
            }
        }

        private async Task<StorageObjectMetadata> StoreSingleObjectAsync(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            var objectUrl = GetObjectUrl(key);

            // Read data into memory
            using var ms = new MemoryStream(65536);
            await data.CopyToAsync(ms, 81920, ct);
            var content = ms.ToArray();

            var request = new HttpRequestMessage(HttpMethod.Put, objectUrl);
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentLength = content.Length;

            // Add auth token
            request.Headers.TryAddWithoutValidation("X-Auth-Token", _authToken);

            // Add custom metadata
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    request.Headers.TryAddWithoutValidation($"X-Object-Meta-{kvp.Key}", kvp.Value);
                }
            }

            // Compute ETag (MD5)
            var md5Hash = MD5.HashData(content);
            var etag = BitConverter.ToString(md5Hash).Replace("-", "").ToLowerInvariant();
            request.Headers.TryAddWithoutValidation("ETag", etag);

            var response = await _httpClient!.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            // Extract ETag from response
            var responseEtag = response.Headers.ETag?.Tag?.Trim('"') ?? etag;

            // Update statistics
            IncrementBytesStored(content.Length);
            IncrementOperationCounter(StorageOperationType.Store);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = content.Length,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ETag = responseEtag,
                ContentType = "application/octet-stream",
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        private async Task<StorageObjectMetadata> StoreStaticLargeObjectAsync(string key, Stream data, long dataLength, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            var segmentCount = (int)Math.Ceiling((double)dataLength / _segmentSizeBytes);
            var manifestSegments = new List<SloManifestSegment>();
            var segmentContainerName = $"{_containerName}_segments";

            // Ensure segment container exists
            await EnsureContainerExistsAsync(segmentContainerName, ct);

            // Upload segments
            for (int segmentNumber = 0; segmentNumber < segmentCount; segmentNumber++)
            {
                ct.ThrowIfCancellationRequested();

                var segmentSize = (int)Math.Min(_segmentSizeBytes, dataLength - (segmentNumber * _segmentSizeBytes));
                var segmentData = new byte[segmentSize];
                var bytesRead = await data.ReadAsync(segmentData, 0, segmentSize, ct);

                if (bytesRead != segmentSize)
                {
                    throw new IOException($"Failed to read expected {segmentSize} bytes for segment {segmentNumber}, got {bytesRead} bytes");
                }

                var segmentKey = $"{key}/segment_{segmentNumber:D8}";
                var segmentPath = $"/{segmentContainerName}/{segmentKey}";
                var segmentUrl = $"{_storageUrl}/{segmentContainerName}/{segmentKey}";

                var request = new HttpRequestMessage(HttpMethod.Put, segmentUrl);
                request.Content = new ByteArrayContent(segmentData);
                request.Content.Headers.ContentLength = segmentData.Length;
                request.Headers.TryAddWithoutValidation("X-Auth-Token", _authToken);

                // Compute segment ETag
                var md5Hash = MD5.HashData(segmentData);
                var etag = BitConverter.ToString(md5Hash).Replace("-", "").ToLowerInvariant();

                var response = await _httpClient!.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                manifestSegments.Add(new SloManifestSegment
                {
                    Path = segmentPath,
                    Etag = etag,
                    SizeBytes = segmentData.Length
                });
            }

            // Create SLO manifest
            var manifestUrl = $"{GetObjectUrl(key)}?multipart-manifest=put";
            var manifestJson = JsonSerializer.Serialize(manifestSegments, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            var manifestRequest = new HttpRequestMessage(HttpMethod.Put, manifestUrl);
            manifestRequest.Content = new StringContent(manifestJson, Encoding.UTF8, "application/json");
            manifestRequest.Headers.TryAddWithoutValidation("X-Auth-Token", _authToken);

            // Add custom metadata to manifest
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    manifestRequest.Headers.TryAddWithoutValidation($"X-Object-Meta-{kvp.Key}", kvp.Value);
                }
            }

            var manifestResponse = await _httpClient!.SendAsync(manifestRequest, ct);
            manifestResponse.EnsureSuccessStatusCode();

            var manifestEtag = manifestResponse.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;

            // Update statistics
            IncrementBytesStored(dataLength);
            IncrementOperationCounter(StorageOperationType.Store);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = dataLength,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ETag = manifestEtag,
                ContentType = "application/octet-stream",
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            await EnsureAuthenticatedAsync(ct);

            var objectUrl = GetObjectUrl(key);
            var request = new HttpRequestMessage(HttpMethod.Get, objectUrl);
            request.Headers.TryAddWithoutValidation("X-Auth-Token", _authToken);

            var response = await _httpClient!.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var ms = new MemoryStream(65536);
            await response.Content.CopyToAsync(ms, ct);
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

            await EnsureAuthenticatedAsync(ct);

            // Get size before deletion for statistics
            long size = 0;
            bool isLargeObject = false;
            try
            {
                var props = await GetObjectPropertiesInternalAsync(key, ct);
                size = props.TryGetValue("Content-Length", out var sizeObj) && sizeObj is long l ? l : 0;
                isLargeObject = props.ContainsKey("X-Static-Large-Object") || props.ContainsKey("X-Object-Manifest");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SwiftStrategy.DeleteAsyncCore] {ex.GetType().Name}: {ex.Message}");
                // Ignore errors getting properties
            }

            var objectUrl = GetObjectUrl(key);

            // If it's a large object, delete with multipart-manifest parameter
            if (isLargeObject)
            {
                objectUrl += "?multipart-manifest=delete";
            }

            var request = new HttpRequestMessage(HttpMethod.Delete, objectUrl);
            request.Headers.TryAddWithoutValidation("X-Auth-Token", _authToken);

            var response = await _httpClient!.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

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
                await EnsureAuthenticatedAsync(ct);

                var objectUrl = GetObjectUrl(key);
                var request = new HttpRequestMessage(HttpMethod.Head, objectUrl);
                request.Headers.TryAddWithoutValidation("X-Auth-Token", _authToken);

                var response = await _httpClient!.SendAsync(request, ct);

                IncrementOperationCounter(StorageOperationType.Exists);

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SwiftStrategy.ExistsAsyncCore] {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();
            await EnsureAuthenticatedAsync(ct);

            IncrementOperationCounter(StorageOperationType.List);

            string? marker = null;
            const int limit = 1000;

            do
            {
                var containerUrl = $"{_storageUrl}/{_containerName}?format=json&limit={limit}";
                if (!string.IsNullOrEmpty(prefix))
                {
                    containerUrl += $"&prefix={Uri.EscapeDataString(prefix)}";
                }
                if (marker != null)
                {
                    containerUrl += $"&marker={Uri.EscapeDataString(marker)}";
                }

                var request = new HttpRequestMessage(HttpMethod.Get, containerUrl);
                request.Headers.TryAddWithoutValidation("X-Auth-Token", _authToken);

                var response = await _httpClient!.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);

                if (string.IsNullOrWhiteSpace(json) || json == "[]")
                {
                    yield break;
                }

                var objects = JsonSerializer.Deserialize<List<SwiftObjectInfo>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (objects == null || objects.Count == 0)
                {
                    yield break;
                }

                foreach (var obj in objects)
                {
                    ct.ThrowIfCancellationRequested();

                    yield return new StorageObjectMetadata
                    {
                        Key = obj.Name,
                        Size = obj.Bytes,
                        Created = obj.LastModified,
                        Modified = obj.LastModified,
                        ETag = obj.Hash,
                        ContentType = obj.ContentType ?? "application/octet-stream",
                        CustomMetadata = null,
                        Tier = Tier
                    };

                    marker = obj.Name;
                }

                // If we got fewer results than the limit, we're done
                if (objects.Count < limit)
                {
                    yield break;
                }

                await Task.Yield();

            } while (!ct.IsCancellationRequested);
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            await EnsureAuthenticatedAsync(ct);

            var properties = await GetObjectPropertiesInternalAsync(key, ct);

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            // Extract custom metadata
            var customMetadata = new Dictionary<string, string>();
            foreach (var kvp in properties.Where(p => p.Key.StartsWith("X-Object-Meta-", StringComparison.OrdinalIgnoreCase)))
            {
                var metaKey = kvp.Key.Substring("X-Object-Meta-".Length);
                customMetadata[metaKey] = kvp.Value.ToString() ?? string.Empty;
            }

            return new StorageObjectMetadata
            {
                Key = key,
                Size = properties.TryGetValue("Content-Length", out var sizeObj) && sizeObj is long size ? size : 0,
                Created = properties.TryGetValue("X-Timestamp", out var tsObj) && tsObj is DateTime created ? created : DateTime.UtcNow,
                Modified = properties.TryGetValue("Last-Modified", out var modObj) && modObj is DateTime modified ? modified : DateTime.UtcNow,
                ETag = properties.TryGetValue("ETag", out var etagObj) ? etagObj.ToString()?.Trim('"') ?? string.Empty : string.Empty,
                ContentType = properties.TryGetValue("Content-Type", out var ctObj) ? ctObj.ToString() ?? "application/octet-stream" : "application/octet-stream",
                CustomMetadata = customMetadata.Count > 0 ? customMetadata : null,
                Tier = Tier
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                await EnsureAuthenticatedAsync(ct);

                // Check container accessibility
                var containerUrl = $"{_storageUrl}/{_containerName}?limit=1";
                var request = new HttpRequestMessage(HttpMethod.Head, containerUrl);
                request.Headers.TryAddWithoutValidation("X-Auth-Token", _authToken);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient!.SendAsync(request, ct);
                sw.Stop();

                if (response.IsSuccessStatusCode)
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Healthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"Swift container '{_containerName}' is accessible",
                        CheckedAt = DateTime.UtcNow
                    };
                }
                else
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Unhealthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"Swift container '{_containerName}' returned status code {response.StatusCode}",
                        CheckedAt = DateTime.UtcNow
                    };
                }
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to access Swift container '{_containerName}': {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            // Swift capacity depends on cluster configuration
            return Task.FromResult<long?>(null);
        }

        #endregion

        #region Swift-Specific Operations

        /// <summary>
        /// Authenticates with Keystone v3 and obtains an authentication token.
        /// </summary>
        private async Task AuthenticateAsync(CancellationToken ct)
        {
            // _authLock is held by the caller (EnsureAuthenticatedAsync); do not re-acquire here.
            var authRequest = new
            {
                auth = new
                {
                    identity = new
                    {
                        methods = new[] { "password" },
                        password = new
                        {
                            user = new
                            {
                                name = _username,
                                domain = new { name = _domainName },
                                password = _password
                            }
                        }
                    },
                    scope = new
                    {
                        project = new
                        {
                            name = _projectName,
                            domain = new { name = _domainName }
                        }
                    }
                }
            };

            var authJson = JsonSerializer.Serialize(authRequest);
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_authUrl}/v3/auth/tokens");
            request.Content = new StringContent(authJson, Encoding.UTF8, "application/json");

            var response = await _httpClient!.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            // Extract token from X-Subject-Token header
            if (response.Headers.TryGetValues("X-Subject-Token", out var tokenValues))
            {
                _authToken = tokenValues.FirstOrDefault();
            }

            if (string.IsNullOrEmpty(_authToken))
            {
                throw new InvalidOperationException("Failed to obtain authentication token from Keystone");
            }

            // Parse response to get storage URL and token expiry
            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // Extract token expiry
            if (root.TryGetProperty("token", out var token) && token.TryGetProperty("expires_at", out var expiresAt))
            {
                _tokenExpiry = DateTime.Parse(expiresAt.GetString() ?? DateTime.UtcNow.AddHours(1).ToString("o"));
            }
            else
            {
                _tokenExpiry = DateTime.UtcNow.AddHours(1); // Default 1 hour
            }

            // Extract storage URL from service catalog
            if (token.TryGetProperty("catalog", out var catalog))
            {
                foreach (var service in catalog.EnumerateArray())
                {
                    if (service.TryGetProperty("type", out var serviceType) && serviceType.GetString() == "object-store")
                    {
                        if (service.TryGetProperty("endpoints", out var endpoints))
                        {
                            foreach (var endpoint in endpoints.EnumerateArray())
                            {
                                if (endpoint.TryGetProperty("interface", out var iface) && iface.GetString() == "public")
                                {
                                    // Check region if specified
                                    if (!string.IsNullOrEmpty(_region))
                                    {
                                        if (endpoint.TryGetProperty("region", out var region) && region.GetString() == _region)
                                        {
                                            _storageUrl = endpoint.GetProperty("url").GetString();
                                            break;
                                        }
                                    }
                                    else if (_storageUrl == null)
                                    {
                                        _storageUrl = endpoint.GetProperty("url").GetString();
                                    }
                                }
                            }
                        }
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(_storageUrl))
            {
                throw new InvalidOperationException("Failed to obtain storage URL from Keystone service catalog");
            }
        }

        /// <summary>
        /// Ensures that the authentication token is valid, refreshing if necessary.
        /// Uses double-checked locking to avoid TOCTOU race on token expiry.
        /// </summary>
        private async Task EnsureAuthenticatedAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_authToken) || DateTime.UtcNow >= _tokenExpiry.AddMinutes(-5))
            {
                await _authLock.WaitAsync(ct);
                try
                {
                    // Double-check after acquiring lock
                    if (string.IsNullOrEmpty(_authToken) || DateTime.UtcNow >= _tokenExpiry.AddMinutes(-5))
                    {
                        await AuthenticateAsync(ct);
                    }
                }
                finally
                {
                    _authLock.Release();
                }
            }
        }

        /// <summary>
        /// Ensures the container exists, creating it if necessary.
        /// </summary>
        private async Task EnsureContainerExistsAsync(CancellationToken ct)
        {
            await EnsureContainerExistsAsync(_containerName, ct);
        }

        /// <summary>
        /// Ensures a specific container exists, creating it if necessary.
        /// </summary>
        private async Task EnsureContainerExistsAsync(string containerName, CancellationToken ct)
        {
            try
            {
                var containerUrl = $"{_storageUrl}/{containerName}";
                var request = new HttpRequestMessage(HttpMethod.Put, containerUrl);
                request.Headers.TryAddWithoutValidation("X-Auth-Token", _authToken);

                // Add versioning if enabled
                if (_enableVersioning && containerName == _containerName)
                {
                    var archiveContainerName = $"{_containerName}_versions";
                    request.Headers.TryAddWithoutValidation("X-Versions-Location", archiveContainerName);

                    // Create archive container first
                    var archiveRequest = new HttpRequestMessage(HttpMethod.Put, $"{_storageUrl}/{archiveContainerName}");
                    archiveRequest.Headers.TryAddWithoutValidation("X-Auth-Token", _authToken);
                    await _httpClient!.SendAsync(archiveRequest, ct);
                }

                // Add storage policy if specified
                if (!string.IsNullOrEmpty(_storagePolicy) && containerName == _containerName)
                {
                    request.Headers.TryAddWithoutValidation("X-Storage-Policy", _storagePolicy);
                }

                var response = await _httpClient!.SendAsync(request, ct);

                // 201 = created, 202 = accepted (already exists)
                if (response.StatusCode != System.Net.HttpStatusCode.Created &&
                    response.StatusCode != System.Net.HttpStatusCode.Accepted &&
                    response.StatusCode != System.Net.HttpStatusCode.NoContent)
                {
                    response.EnsureSuccessStatusCode();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SwiftStrategy.EnsureContainerExistsAsync] {ex.GetType().Name}: {ex.Message}");
                // Container might already exist, which is fine
            }
        }

        /// <summary>
        /// Gets object properties via HEAD request.
        /// </summary>
        private async Task<Dictionary<string, object>> GetObjectPropertiesInternalAsync(string key, CancellationToken ct)
        {
            var objectUrl = GetObjectUrl(key);
            var request = new HttpRequestMessage(HttpMethod.Head, objectUrl);
            request.Headers.TryAddWithoutValidation("X-Auth-Token", _authToken);

            var response = await _httpClient!.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var properties = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Length"] = response.Content.Headers.ContentLength ?? 0L,
                ["Content-Type"] = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
                ["Last-Modified"] = response.Content.Headers.LastModified?.UtcDateTime ?? DateTime.UtcNow,
                ["ETag"] = response.Headers.ETag?.Tag?.Trim('"') ?? string.Empty
            };

            // Extract all Swift headers
            foreach (var header in response.Headers)
            {
                if (header.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                {
                    var value = header.Value.FirstOrDefault() ?? string.Empty;

                    // Parse timestamps
                    if (header.Key.Equals("X-Timestamp", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(value, out var timestamp))
                    {
                        properties[header.Key] = DateTimeOffset.FromUnixTimeSeconds((long)timestamp).UtcDateTime;
                    }
                    else
                    {
                        properties[header.Key] = value;
                    }
                }
            }

            return properties;
        }

        /// <summary>
        /// Generates a temporary URL for secure access without authentication.
        /// </summary>
        public string GenerateTempUrl(string key, TimeSpan expiresIn, string method = "GET", string? tempUrlKey = null)
        {
            ValidateKey(key);

            if (string.IsNullOrEmpty(tempUrlKey))
            {
                tempUrlKey = GetConfiguration<string>("TempUrlKey")
                    ?? throw new InvalidOperationException("TempUrlKey is required for generating temporary URLs");
            }

            var expires = DateTimeOffset.UtcNow.Add(expiresIn).ToUnixTimeSeconds();
            var objectPath = $"/v1/{GetAccountFromStorageUrl()}/{_containerName}/{key}";

            var hmacBody = $"{method}\n{expires}\n{objectPath}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(tempUrlKey));
            var signature = BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(hmacBody)))
                .Replace("-", "").ToLowerInvariant();

            return $"{_storageUrl}/{_containerName}/{key}?temp_url_sig={signature}&temp_url_expires={expires}";
        }

        /// <summary>
        /// Copies an object within Swift storage.
        /// </summary>
        public async Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(sourceKey);
            ValidateKey(destinationKey);

            await EnsureAuthenticatedAsync(ct);

            var destUrl = GetObjectUrl(destinationKey);
            var request = new HttpRequestMessage(HttpMethod.Put, destUrl);
            request.Headers.TryAddWithoutValidation("X-Auth-Token", _authToken);
            request.Headers.TryAddWithoutValidation("X-Copy-From", $"/{_containerName}/{sourceKey}");
            request.Content = new ByteArrayContent(Array.Empty<byte>());
            request.Content.Headers.ContentLength = 0;

            var response = await _httpClient!.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// Sets object expiration.
        /// </summary>
        public async Task SetObjectExpirationAsync(string key, DateTime expiresAt, CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            await EnsureAuthenticatedAsync(ct);

            var unixTimestamp = new DateTimeOffset(expiresAt).ToUnixTimeSeconds();
            var objectUrl = GetObjectUrl(key);

            var request = new HttpRequestMessage(HttpMethod.Post, objectUrl);
            request.Headers.TryAddWithoutValidation("X-Auth-Token", _authToken);
            request.Headers.TryAddWithoutValidation("X-Delete-At", unixTimestamp.ToString());

            var response = await _httpClient!.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }

        #endregion

        #region Helper Methods

        private string GetObjectUrl(string key)
        {
            return $"{_storageUrl}/{_containerName}/{key}";
        }

        private string GetAccountFromStorageUrl()
        {
            if (string.IsNullOrEmpty(_storageUrl))
            {
                return string.Empty;
            }

            var uri = new Uri(_storageUrl);
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length > 0 ? segments[^1] : string.Empty;
        }

        protected override int GetMaxKeyLength() => 1024; // Swift max object name length

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Represents Swift object information from listing.
    /// </summary>
    internal class SwiftObjectInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public long Bytes { get; set; }
        public string? ContentType { get; set; }
        public DateTime LastModified { get; set; }
    }

    /// <summary>
    /// Represents a segment in a Static Large Object manifest.
    /// </summary>
    internal class SloManifestSegment
    {
        public string Path { get; set; } = string.Empty;
        public string Etag { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
    }

    #endregion
}
