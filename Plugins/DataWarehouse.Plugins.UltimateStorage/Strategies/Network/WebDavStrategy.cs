using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Network
{
    /// <summary>
    /// WebDAV (Web Distributed Authoring and Versioning) storage strategy with production-ready features:
    /// - HTTP/HTTPS with Basic, Digest, and NTLM authentication
    /// - RFC 4918 compliant WebDAV operations (PROPFIND, PROPPATCH, MKCOL, etc.)
    /// - Resource locking support (LOCK/UNLOCK methods)
    /// - Metadata management via WebDAV properties
    /// - COPY and MOVE operations
    /// - Collection (directory) management
    /// - Connection pooling and persistent connections
    /// - Retry logic with exponential backoff
    /// - Comprehensive error handling
    /// - Support for popular WebDAV servers (ownCloud, Nextcloud, Apache, IIS)
    /// </summary>
    public class WebDavStrategy : UltimateStorageStrategyBase
    {
        private HttpClient? _httpClient;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private readonly SemaphoreSlim _writeLock = new(10, 10); // Allow 10 concurrent writes
        private readonly Dictionary<string, WebDavLock> _activeLocks = new();
        private readonly SemaphoreSlim _lockManager = new(1, 1);

        private string _baseUrl = string.Empty;
        private string _basePath = string.Empty;
        private string _username = string.Empty;
        private string _password = string.Empty;
        private WebDavAuthType _authType = WebDavAuthType.Basic;
        private bool _useHttps = true;
        private int _timeoutSeconds = 60;
        private bool _useLocking = true;
        private int _lockTimeoutSeconds = 300; // 5 minutes default
        private int _maxRetries = 3;
        private int _bufferSize = 81920; // 80KB default
        private bool _validateServerCertificate = true;
        private string? _domain = null; // For NTLM authentication

        public override string StrategyId => "webdav";
        public override string Name => "WebDAV Network Storage";
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = true,
            SupportsVersioning = false, // Depends on WebDAV server
            SupportsTiering = false,
            SupportsEncryption = true, // HTTPS
            SupportsCompression = false,
            SupportsMultipart = false,
            MaxObjectSize = null, // Limited by WebDAV server
            MaxObjects = null,
            ConsistencyModel = ConsistencyModel.Strong
        };

        /// <summary>
        /// Initializes the WebDAV storage strategy.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load required configuration
            _baseUrl = GetConfiguration<string>("BaseUrl", string.Empty);

            if (string.IsNullOrWhiteSpace(_baseUrl))
            {
                throw new InvalidOperationException("WebDAV base URL is required. Set 'BaseUrl' in configuration (e.g., 'https://webdav.example.com').");
            }

            // Parse and validate URL
            if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var baseUri))
            {
                throw new InvalidOperationException($"Invalid WebDAV base URL: {_baseUrl}");
            }

            _useHttps = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

            // Load authentication configuration
            _username = GetConfiguration<string>("Username", string.Empty);
            _password = GetConfiguration<string>("Password", string.Empty);
            _domain = GetConfiguration<string?>("Domain", null);

            var authTypeStr = GetConfiguration<string>("AuthType", "Basic");
            _authType = authTypeStr.ToLowerInvariant() switch
            {
                "basic" => WebDavAuthType.Basic,
                "digest" => WebDavAuthType.Digest,
                "ntlm" => WebDavAuthType.NTLM,
                "none" => WebDavAuthType.None,
                _ => WebDavAuthType.Basic
            };

            if (_authType != WebDavAuthType.None && string.IsNullOrWhiteSpace(_username))
            {
                throw new InvalidOperationException("WebDAV authentication credentials are required. Set 'Username' and 'Password' in configuration, or set 'AuthType' to 'None'.");
            }

            // Load optional configuration
            _basePath = GetConfiguration<string>("BasePath", string.Empty);
            _timeoutSeconds = GetConfiguration<int>("TimeoutSeconds", 60);
            _useLocking = GetConfiguration<bool>("UseLocking", true);
            _lockTimeoutSeconds = GetConfiguration<int>("LockTimeoutSeconds", 300);
            _maxRetries = GetConfiguration<int>("MaxRetries", 3);
            _bufferSize = GetConfiguration<int>("BufferSize", 81920);
            _validateServerCertificate = GetConfiguration<bool>("ValidateServerCertificate", true);

            // Normalize base path
            _basePath = NormalizePath(_basePath);

            // Ensure base URL doesn't end with slash
            _baseUrl = _baseUrl.TrimEnd('/');

            // Initialize HTTP client
            await InitializeHttpClientAsync(ct);

            // Ensure base path collection exists
            if (!string.IsNullOrEmpty(_basePath))
            {
                await EnsureCollectionExistsAsync(_basePath, ct);
            }
        }

        #region HTTP Client Management

        /// <summary>
        /// Initializes the HTTP client with appropriate authentication.
        /// </summary>
        private async Task InitializeHttpClientAsync(CancellationToken ct)
        {
            await _connectionLock.WaitAsync(ct);
            try
            {
                // Dispose existing client if any
                _httpClient?.Dispose();

                // Create handler with authentication
                var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 5,
                    UseCookies = true,
                    PreAuthenticate = true
                };

                // Configure certificate validation
                // SECURITY: TLS certificate validation is enabled by default (_validateServerCertificate = true).
                if (!_validateServerCertificate)
                {
                    handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
                }

                // Configure authentication
                if (_authType != WebDavAuthType.None)
                {
                    var credentials = !string.IsNullOrEmpty(_domain)
                        ? new NetworkCredential(_username, _password, _domain)
                        : new NetworkCredential(_username, _password);

                    handler.Credentials = credentials;

                    if (_authType == WebDavAuthType.NTLM)
                    {
                        handler.UseDefaultCredentials = false;
                    }
                }

                // Create HTTP client
                _httpClient = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(_timeoutSeconds)
                };

                // Set default headers
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DataWarehouse-UltimateStorage-WebDAV/1.0");
                _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

                // Add Basic authentication header if specified
                if (_authType == WebDavAuthType.Basic && !string.IsNullOrEmpty(_username))
                {
                    var authBytes = Encoding.UTF8.GetBytes($"{_username}:{_password}");
                    var authHeader = Convert.ToBase64String(authBytes);
                    _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        #endregion

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            ValidateKey(key);
            ValidateStream(data);

            var resourceUrl = GetResourceUrl(key);
            var collectionUrl = GetParentCollectionUrl(key);

            await _writeLock.WaitAsync(ct);
            try
            {
                // Ensure parent collection exists
                if (!string.IsNullOrEmpty(collectionUrl))
                {
                    await EnsureCollectionExistsAsync(GetPathFromUrl(collectionUrl), ct);
                }

                // Acquire lock if enabled
                WebDavLock? lockToken = null;
                if (_useLocking)
                {
                    lockToken = await AcquireLockAsync(resourceUrl, ct);
                }

                try
                {
                    // Upload file using PUT
                    var request = new HttpRequestMessage(HttpMethod.Put, resourceUrl);
                    request.Content = new StreamContent(data, _bufferSize);

                    // Add lock token if present
                    if (lockToken != null)
                    {
                        request.Headers.Add("If", $"(<{lockToken.Token}>)");
                    }

                    // Detect content type
                    var contentType = GetContentType(key);
                    if (!string.IsNullOrEmpty(contentType))
                    {
                        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                    }

                    var response = await ExecuteWithRetryAsync(request, ct);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new IOException($"Failed to store WebDAV resource '{key}'. Status: {response.StatusCode}, Reason: {response.ReasonPhrase}");
                    }

                    var size = data.CanSeek ? data.Length : 0;

                    // Store custom metadata via PROPPATCH if provided
                    if (metadata != null && metadata.Count > 0)
                    {
                        await SetPropertiesAsync(resourceUrl, metadata, lockToken, ct);
                    }

                    // Get resource properties to build metadata
                    var properties = await GetPropertiesAsync(resourceUrl, ct);

                    // Update statistics
                    IncrementBytesStored(size);
                    IncrementOperationCounter(StorageOperationType.Store);

                    return new StorageObjectMetadata
                    {
                        Key = key,
                        Size = properties.ContentLength ?? size,
                        Created = properties.CreationDate ?? DateTime.UtcNow,
                        Modified = properties.LastModified ?? DateTime.UtcNow,
                        ETag = properties.ETag ?? GenerateETag(key, properties.LastModified ?? DateTime.UtcNow),
                        ContentType = properties.ContentType ?? contentType,
                        CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                        Tier = Tier
                    };
                }
                finally
                {
                    // Release lock
                    if (lockToken != null)
                    {
                        await ReleaseLockAsync(resourceUrl, lockToken, ct);
                    }
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            var resourceUrl = GetResourceUrl(key);

            // Acquire shared lock if enabled
            WebDavLock? lockToken = null;
            if (_useLocking)
            {
                lockToken = await AcquireLockAsync(resourceUrl, ct, isShared: true);
            }

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, resourceUrl);

                // Add lock token if present
                if (lockToken != null)
                {
                    request.Headers.Add("If", $"(<{lockToken.Token}>)");
                }

                var response = await ExecuteWithRetryAsync(request, ct);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new FileNotFoundException($"WebDAV resource not found: {key}", key);
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new IOException($"Failed to retrieve WebDAV resource '{key}'. Status: {response.StatusCode}, Reason: {response.ReasonPhrase}");
                }

                // Read content into memory stream to allow multiple reads
                var memoryStream = new MemoryStream(65536);
                await response.Content.CopyToAsync(memoryStream, ct);
                memoryStream.Position = 0;

                // Update statistics
                IncrementBytesRetrieved(memoryStream.Length);
                IncrementOperationCounter(StorageOperationType.Retrieve);

                // Wrap stream to release lock on disposal
                return new WebDavLockReleaseStream(memoryStream, lockToken, this, resourceUrl);
            }
            catch
            {
                if (lockToken != null)
                {
                    await ReleaseLockAsync(resourceUrl, lockToken, ct);
                }
                throw;
            }
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            var resourceUrl = GetResourceUrl(key);

            // Get size before deletion for statistics
            long size = 0;
            try
            {
                var properties = await GetPropertiesAsync(resourceUrl, ct);
                size = properties.ContentLength ?? 0;
            }
            catch
            {
                // Ignore if metadata retrieval fails
            }

            // Acquire exclusive lock if enabled
            WebDavLock? lockToken = null;
            if (_useLocking)
            {
                lockToken = await AcquireLockAsync(resourceUrl, ct);
            }

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Delete, resourceUrl);

                // Add lock token if present
                if (lockToken != null)
                {
                    request.Headers.Add("If", $"(<{lockToken.Token}>)");
                }

                var response = await ExecuteWithRetryAsync(request, ct);

                // Accept 404 as success (already deleted)
                if (response.StatusCode != HttpStatusCode.NotFound && !response.IsSuccessStatusCode)
                {
                    throw new IOException($"Failed to delete WebDAV resource '{key}'. Status: {response.StatusCode}, Reason: {response.ReasonPhrase}");
                }

                // Update statistics
                if (size > 0)
                {
                    IncrementBytesDeleted(size);
                }
                IncrementOperationCounter(StorageOperationType.Delete);
            }
            finally
            {
                // Release lock
                if (lockToken != null)
                {
                    await ReleaseLockAsync(resourceUrl, lockToken, ct);
                }
            }
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            var resourceUrl = GetResourceUrl(key);

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Head, resourceUrl);
                var response = await ExecuteWithRetryAsync(request, ct);

                IncrementOperationCounter(StorageOperationType.Exists);

                return response.IsSuccessStatusCode;
            }
            catch
            {
                IncrementOperationCounter(StorageOperationType.Exists);
                return false;
            }
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.List);

            var collectionPath = string.IsNullOrEmpty(prefix)
                ? _basePath
                : CombinePaths(_basePath, prefix);

            var collectionUrl = GetResourceUrl(collectionPath);

            // Use PROPFIND to list collection contents
            var items = await ListCollectionAsync(collectionUrl, prefix ?? string.Empty, ct);

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            var resourceUrl = GetResourceUrl(key);
            var properties = await GetPropertiesAsync(resourceUrl, ct);

            if (properties.IsCollection)
            {
                throw new InvalidOperationException($"WebDAV resource is a collection (directory), not a file: {key}");
            }

            // Load custom properties
            var customMetadata = await GetCustomPropertiesAsync(resourceUrl, ct);

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = properties.ContentLength ?? 0,
                Created = properties.CreationDate ?? DateTime.MinValue,
                Modified = properties.LastModified ?? DateTime.MinValue,
                ETag = properties.ETag ?? string.Empty,
                ContentType = properties.ContentType,
                CustomMetadata = customMetadata,
                Tier = Tier
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Perform OPTIONS request to check server capabilities
                var request = new HttpRequestMessage(HttpMethod.Options, _baseUrl);
                var response = await ExecuteWithRetryAsync(request, ct);

                sw.Stop();

                if (response.IsSuccessStatusCode)
                {
                    var davHeader = response.Headers.GetValues("DAV").FirstOrDefault();
                    var allowHeader = response.Headers.GetValues("Allow").FirstOrDefault();

                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Healthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"WebDAV server at {_baseUrl} is accessible. Capabilities: DAV {davHeader}, Methods: {allowHeader}",
                        CheckedAt = DateTime.UtcNow
                    };
                }
                else
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Degraded,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"WebDAV server responded with status: {response.StatusCode}",
                        CheckedAt = DateTime.UtcNow
                    };
                }
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to connect to WebDAV server at {_baseUrl}: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override async Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            try
            {
                // Try to get quota information via PROPFIND
                var resourceUrl = GetResourceUrl(_basePath);
                var propfindXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<D:propfind xmlns:D=""DAV:"">
  <D:prop>
    <D:quota-available-bytes/>
  </D:prop>
</D:propfind>";

                var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), resourceUrl);
                request.Headers.Add("Depth", "0");
                request.Content = new StringContent(propfindXml, Encoding.UTF8, "application/xml");

                var response = await ExecuteWithRetryAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    var responseXml = await response.Content.ReadAsStringAsync(ct);
                    var doc = XDocument.Parse(responseXml);
                    var ns = XNamespace.Get("DAV:");

                    var quotaElement = doc.Descendants(ns + "quota-available-bytes").FirstOrDefault();
                    if (quotaElement != null && long.TryParse(quotaElement.Value, out var quota))
                    {
                        return quota;
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region WebDAV Protocol Operations

        /// <summary>
        /// Ensures a collection (directory) exists, creating it if necessary.
        /// </summary>
        private async Task EnsureCollectionExistsAsync(string path, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(path))
                return;

            var collectionUrl = GetResourceUrl(path);

            // Check if collection exists using PROPFIND
            var checkRequest = new HttpRequestMessage(new HttpMethod("PROPFIND"), collectionUrl);
            checkRequest.Headers.Add("Depth", "0");

            var checkResponse = await ExecuteWithRetryAsync(checkRequest, ct);

            if (checkResponse.IsSuccessStatusCode)
            {
                // Collection exists
                return;
            }

            // Create parent collections recursively
            var parentPath = GetParentPath(path);
            if (!string.IsNullOrEmpty(parentPath))
            {
                await EnsureCollectionExistsAsync(parentPath, ct);
            }

            // Create collection using MKCOL
            var request = new HttpRequestMessage(new HttpMethod("MKCOL"), collectionUrl);
            var response = await ExecuteWithRetryAsync(request, ct);

            if (response.StatusCode != HttpStatusCode.MethodNotAllowed && !response.IsSuccessStatusCode)
            {
                throw new IOException($"Failed to create WebDAV collection '{path}'. Status: {response.StatusCode}, Reason: {response.ReasonPhrase}");
            }
        }

        /// <summary>
        /// Gets properties of a WebDAV resource using PROPFIND.
        /// </summary>
        private async Task<WebDavProperties> GetPropertiesAsync(string resourceUrl, CancellationToken ct)
        {
            var propfindXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<D:propfind xmlns:D=""DAV:"">
  <D:prop>
    <D:displayname/>
    <D:getcontentlength/>
    <D:getcontenttype/>
    <D:creationdate/>
    <D:getlastmodified/>
    <D:getetag/>
    <D:resourcetype/>
  </D:prop>
</D:propfind>";

            var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), resourceUrl);
            request.Headers.Add("Depth", "0");
            request.Content = new StringContent(propfindXml, Encoding.UTF8, "application/xml");

            var response = await ExecuteWithRetryAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new FileNotFoundException($"WebDAV resource not found at URL: {resourceUrl}");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new IOException($"Failed to get WebDAV properties. Status: {response.StatusCode}, Reason: {response.ReasonPhrase}");
            }

            var responseXml = await response.Content.ReadAsStringAsync(ct);
            return ParsePropertiesFromXml(responseXml);
        }

        /// <summary>
        /// Sets custom properties on a WebDAV resource using PROPPATCH.
        /// </summary>
        private async Task SetPropertiesAsync(string resourceUrl, IDictionary<string, string> properties, WebDavLock? lockToken, CancellationToken ct)
        {
            if (properties == null || properties.Count == 0)
                return;

            var ns = XNamespace.Get("http://datawarehouse.com/ns/");
            var davNs = XNamespace.Get("DAV:");

            var proppatchDoc = new XDocument(
                new XElement(davNs + "propertyupdate",
                    new XAttribute(XNamespace.Xmlns + "D", davNs.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "C", ns.NamespaceName),
                    new XElement(davNs + "set",
                        new XElement(davNs + "prop",
                            properties.Select(kvp => new XElement(ns + SanitizePropertyName(kvp.Key), kvp.Value))
                        )
                    )
                )
            );

            var request = new HttpRequestMessage(new HttpMethod("PROPPATCH"), resourceUrl);
            request.Content = new StringContent(proppatchDoc.ToString(), Encoding.UTF8, "application/xml");

            // Add lock token if present
            if (lockToken != null)
            {
                request.Headers.Add("If", $"(<{lockToken.Token}>)");
            }

            var response = await ExecuteWithRetryAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                // Non-fatal: log but don't throw
                // Some WebDAV servers don't support custom properties
            }
        }

        /// <summary>
        /// Gets custom properties from a WebDAV resource.
        /// </summary>
        private async Task<IReadOnlyDictionary<string, string>?> GetCustomPropertiesAsync(string resourceUrl, CancellationToken ct)
        {
            try
            {
                var ns = XNamespace.Get("http://datawarehouse.com/ns/");
                var davNs = XNamespace.Get("DAV:");

                var propfindXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<D:propfind xmlns:D=""DAV:"" xmlns:C=""{ns.NamespaceName}"">
  <D:allprop/>
</D:propfind>";

                var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), resourceUrl);
                request.Headers.Add("Depth", "0");
                request.Content = new StringContent(propfindXml, Encoding.UTF8, "application/xml");

                var response = await ExecuteWithRetryAsync(request, ct);

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var responseXml = await response.Content.ReadAsStringAsync(ct);
                var doc = XDocument.Parse(responseXml);

                var customProps = new Dictionary<string, string>();

                // Extract custom namespace properties
                var propElements = doc.Descendants(ns.NamespaceName).Elements();
                foreach (var element in propElements)
                {
                    if (!string.IsNullOrEmpty(element.Value))
                    {
                        customProps[element.Name.LocalName] = element.Value;
                    }
                }

                return customProps.Count > 0 ? customProps : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Lists contents of a WebDAV collection using PROPFIND.
        /// </summary>
        private async Task<List<StorageObjectMetadata>> ListCollectionAsync(string collectionUrl, string prefix, CancellationToken ct)
        {
            var results = new List<StorageObjectMetadata>();

            var propfindXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<D:propfind xmlns:D=""DAV:"">
  <D:prop>
    <D:displayname/>
    <D:getcontentlength/>
    <D:getcontenttype/>
    <D:creationdate/>
    <D:getlastmodified/>
    <D:getetag/>
    <D:resourcetype/>
  </D:prop>
</D:propfind>";

            var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), collectionUrl);
            request.Headers.Add("Depth", "infinity"); // Recursive listing
            request.Content = new StringContent(propfindXml, Encoding.UTF8, "application/xml");

            var response = await ExecuteWithRetryAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                return results;
            }

            var responseXml = await response.Content.ReadAsStringAsync(ct);
            var doc = XDocument.Parse(responseXml);
            var ns = XNamespace.Get("DAV:");

            foreach (var responseElement in doc.Descendants(ns + "response"))
            {
                var href = responseElement.Element(ns + "href")?.Value;
                if (string.IsNullOrEmpty(href))
                    continue;

                // Skip the collection itself
                if (href.TrimEnd('/').Equals(collectionUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                    href.TrimEnd('/').EndsWith(_basePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    continue;

                var propstat = responseElement.Element(ns + "propstat");
                if (propstat == null)
                    continue;

                var prop = propstat.Element(ns + "prop");
                if (prop == null)
                    continue;

                // Check if it's a collection
                var resourceType = prop.Element(ns + "resourcetype");
                var isCollection = resourceType?.Element(ns + "collection") != null;

                if (isCollection)
                    continue; // Skip collections in file listing

                // Extract properties
                var contentLength = prop.Element(ns + "getcontentlength")?.Value;
                var contentType = prop.Element(ns + "getcontenttype")?.Value;
                var creationDate = prop.Element(ns + "creationdate")?.Value;
                var lastModified = prop.Element(ns + "getlastmodified")?.Value;
                var etag = prop.Element(ns + "getetag")?.Value;

                // Calculate relative key
                var decodedHref = Uri.UnescapeDataString(href);
                var baseUrlPath = new Uri(_baseUrl).AbsolutePath;
                var relativePath = decodedHref.StartsWith(baseUrlPath) ? decodedHref.Substring(baseUrlPath.Length) : decodedHref;
                relativePath = relativePath.TrimStart('/');

                if (!string.IsNullOrEmpty(_basePath))
                {
                    var normalizedBasePath = _basePath.Trim('/');
                    if (relativePath.StartsWith(normalizedBasePath + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        relativePath = relativePath.Substring(normalizedBasePath.Length + 1);
                    }
                }

                var key = relativePath.Replace('\\', '/').TrimStart('/');

                // Filter by prefix
                if (!string.IsNullOrEmpty(prefix) && !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add(new StorageObjectMetadata
                {
                    Key = key,
                    Size = long.TryParse(contentLength, out var size) ? size : 0,
                    Created = TryParseDate(creationDate) ?? DateTime.MinValue,
                    Modified = TryParseDate(lastModified) ?? DateTime.MinValue,
                    ETag = etag?.Trim('"'),
                    ContentType = contentType,
                    Tier = Tier
                });
            }

            return results;
        }

        /// <summary>
        /// Acquires a WebDAV lock on a resource using LOCK method.
        /// </summary>
        private async Task<WebDavLock?> AcquireLockAsync(string resourceUrl, CancellationToken ct, bool isShared = false)
        {
            if (!_useLocking)
                return null;

            await _lockManager.WaitAsync(ct);
            try
            {
                var lockKey = resourceUrl.ToLowerInvariant();

                // Check if already locked
                if (_activeLocks.TryGetValue(lockKey, out var existingLock))
                {
                    // Reuse existing lock if it's still valid
                    if (existingLock.ExpiresAt > DateTime.UtcNow)
                    {
                        return existingLock;
                    }
                    else
                    {
                        // Remove expired lock
                        _activeLocks.Remove(lockKey);
                    }
                }

                var davNs = XNamespace.Get("DAV:");
                var lockScope = isShared ? "shared" : "exclusive";

                var lockXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<D:lockinfo xmlns:D=""DAV:"">
  <D:lockscope><D:{lockScope}/></D:lockscope>
  <D:locktype><D:write/></D:locktype>
  <D:owner>
    <D:href>mailto:{_username}</D:href>
  </D:owner>
</D:lockinfo>";

                var request = new HttpRequestMessage(new HttpMethod("LOCK"), resourceUrl);
                request.Headers.Add("Timeout", $"Second-{_lockTimeoutSeconds}");
                request.Headers.Add("Depth", "0");
                request.Content = new StringContent(lockXml, Encoding.UTF8, "application/xml");

                var response = await ExecuteWithRetryAsync(request, ct);

                if (!response.IsSuccessStatusCode)
                {
                    // Lock failed, but don't throw - locking may not be supported
                    return null;
                }

                var responseXml = await response.Content.ReadAsStringAsync(ct);
                var doc = XDocument.Parse(responseXml);
                var ns = XNamespace.Get("DAV:");

                var lockTokenElement = doc.Descendants(ns + "locktoken").FirstOrDefault();
                var hrefElement = lockTokenElement?.Element(ns + "href");
                var lockToken = hrefElement?.Value;

                if (string.IsNullOrEmpty(lockToken))
                {
                    return null;
                }

                // Clean up lock token format
                lockToken = lockToken.Replace("opaquelocktoken:", "").Trim();

                var webDavLock = new WebDavLock
                {
                    Token = lockToken,
                    ResourceUrl = resourceUrl,
                    AcquiredAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddSeconds(_lockTimeoutSeconds),
                    IsShared = isShared
                };

                _activeLocks[lockKey] = webDavLock;

                return webDavLock;
            }
            catch
            {
                // Lock acquisition failed, but continue without locking
                return null;
            }
            finally
            {
                _lockManager.Release();
            }
        }

        /// <summary>
        /// Releases a WebDAV lock using UNLOCK method.
        /// </summary>
        internal async Task ReleaseLockAsync(string resourceUrl, WebDavLock lockToken, CancellationToken ct)
        {
            if (lockToken == null || !_useLocking)
                return;

            await _lockManager.WaitAsync(ct);
            try
            {
                var request = new HttpRequestMessage(new HttpMethod("UNLOCK"), resourceUrl);
                request.Headers.Add("Lock-Token", $"<{lockToken.Token}>");

                await ExecuteWithRetryAsync(request, ct);

                var lockKey = resourceUrl.ToLowerInvariant();
                _activeLocks.Remove(lockKey);
            }
            catch
            {
                // Ignore unlock errors
            }
            finally
            {
                _lockManager.Release();
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Executes an HTTP request with retry logic and exponential backoff.
        /// </summary>
        private async Task<HttpResponseMessage> ExecuteWithRetryAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var attempt = 0;
            Exception? lastException = null;

            while (attempt < _maxRetries)
            {
                try
                {
                    // Clone request for retry (HttpRequestMessage can only be sent once)
                    var clonedRequest = await CloneHttpRequestAsync(request);
                    var response = await _httpClient!.SendAsync(clonedRequest, ct);

                    // Don't retry on client errors (4xx) except 408, 429
                    if (response.IsSuccessStatusCode ||
                        ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500 &&
                         response.StatusCode != HttpStatusCode.RequestTimeout &&
                         response.StatusCode != (HttpStatusCode)429))
                    {
                        return response;
                    }

                    lastException = new HttpRequestException($"HTTP {response.StatusCode}: {response.ReasonPhrase}");
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                {
                    lastException = ex;
                }

                attempt++;

                if (attempt < _maxRetries)
                {
                    // Exponential backoff: 1s, 2s, 4s
                    var delayMs = (int)Math.Pow(2, attempt - 1) * 1000;
                    await Task.Delay(delayMs, ct);
                }
            }

            throw new IOException($"Failed to execute WebDAV request after {_maxRetries} attempts", lastException);
        }

        /// <summary>
        /// Clones an HTTP request for retry purposes.
        /// </summary>
        private async Task<HttpRequestMessage> CloneHttpRequestAsync(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);

            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content != null)
            {
                var contentBytes = await request.Content.ReadAsByteArrayAsync();
                clone.Content = new ByteArrayContent(contentBytes);

                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }

        private string GetResourceUrl(string path)
        {
            var normalizedPath = NormalizePath(path);

            // Path traversal protection — reject traversal sequences in keys
            ValidatePathSafe(normalizedPath);

            var fullPath = string.IsNullOrEmpty(normalizedPath)
                ? _baseUrl
                : $"{_baseUrl}/{normalizedPath}";

            // Validate the constructed URL stays within the base URL scope
            var baseUri = new Uri(_baseUrl.TrimEnd('/') + "/");
            var fullUri = new Uri(fullPath);
            if (!fullUri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Path traversal attempt detected");

            return fullPath;
        }

        /// <summary>
        /// Validates that a path does not contain traversal sequences.
        /// </summary>
        private static void ValidatePathSafe(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            // Reject directory traversal sequences (raw and URL-encoded)
            if (path.Contains("..") ||
                path.Contains("//") ||
                path.Contains('\\') ||
                path.Contains("%2e%2e", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("%5c", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Path traversal attempt detected");
            }
        }

        private string GetParentCollectionUrl(string key)
        {
            var parentPath = GetParentPath(key);
            return string.IsNullOrEmpty(parentPath) ? string.Empty : GetResourceUrl(parentPath);
        }

        private string GetPathFromUrl(string url)
        {
            var uri = new Uri(url);
            var baseUri = new Uri(_baseUrl);
            var relativePath = baseUri.MakeRelativeUri(uri).ToString();
            return Uri.UnescapeDataString(relativePath);
        }

        private string GetParentPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            var normalized = path.Trim('/');
            var lastSlash = normalized.LastIndexOf('/');

            return lastSlash < 0 ? string.Empty : normalized.Substring(0, lastSlash);
        }

        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            return path.Trim('/').Replace('\\', '/');
        }

        private string CombinePaths(string path1, string path2)
        {
            if (string.IsNullOrEmpty(path1))
                return NormalizePath(path2);
            if (string.IsNullOrEmpty(path2))
                return NormalizePath(path1);

            return $"{NormalizePath(path1)}/{NormalizePath(path2)}";
        }

        private string SanitizePropertyName(string name)
        {
            // Remove invalid XML characters
            return new string(name.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        }

        private WebDavProperties ParsePropertiesFromXml(string xml)
        {
            var doc = XDocument.Parse(xml);
            var ns = XNamespace.Get("DAV:");

            var prop = doc.Descendants(ns + "prop").FirstOrDefault();
            if (prop == null)
            {
                return new WebDavProperties();
            }

            var resourceType = prop.Element(ns + "resourcetype");
            var isCollection = resourceType?.Element(ns + "collection") != null;

            return new WebDavProperties
            {
                DisplayName = prop.Element(ns + "displayname")?.Value,
                ContentLength = long.TryParse(prop.Element(ns + "getcontentlength")?.Value, out var len) ? len : null,
                ContentType = prop.Element(ns + "getcontenttype")?.Value,
                CreationDate = TryParseDate(prop.Element(ns + "creationdate")?.Value),
                LastModified = TryParseDate(prop.Element(ns + "getlastmodified")?.Value),
                ETag = prop.Element(ns + "getetag")?.Value?.Trim('"'),
                IsCollection = isCollection
            };
        }

        private DateTime? TryParseDate(string? dateString)
        {
            if (string.IsNullOrEmpty(dateString))
                return null;

            if (DateTime.TryParse(dateString, out var date))
                return date.ToUniversalTime();

            return null;
        }

        private string GenerateETag(string key, DateTime lastModified)
        {
            var hash = HashCode.Combine(key, lastModified.Ticks);
            return hash.ToString("x");
        }

        private string? GetContentType(string key)
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

        #endregion

        #region Cleanup

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();

            // Release all active locks
            var lockTasks = _activeLocks.Values.Select(async lockToken =>
            {
                try
                {
                    await ReleaseLockAsync(lockToken.ResourceUrl, lockToken, CancellationToken.None);
                }
                catch
                {
                    // Ignore errors during cleanup
                }
            });

            await Task.WhenAll(lockTasks);
            _activeLocks.Clear();

            _httpClient?.Dispose();
            _connectionLock?.Dispose();
            _writeLock?.Dispose();
            _lockManager?.Dispose();
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// WebDAV authentication type.
    /// </summary>
    public enum WebDavAuthType
    {
        /// <summary>No authentication.</summary>
        None,

        /// <summary>HTTP Basic authentication.</summary>
        Basic,

        /// <summary>HTTP Digest authentication.</summary>
        Digest,

        /// <summary>NTLM authentication (Windows).</summary>
        NTLM
    }

    /// <summary>
    /// WebDAV resource properties.
    /// </summary>
    internal class WebDavProperties
    {
        public string? DisplayName { get; set; }
        public long? ContentLength { get; set; }
        public string? ContentType { get; set; }
        public DateTime? CreationDate { get; set; }
        public DateTime? LastModified { get; set; }
        public string? ETag { get; set; }
        public bool IsCollection { get; set; }
    }

    /// <summary>
    /// Represents a WebDAV lock token.
    /// </summary>
    internal class WebDavLock
    {
        public string Token { get; set; } = string.Empty;
        public string ResourceUrl { get; set; } = string.Empty;
        public DateTime AcquiredAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public bool IsShared { get; set; }
    }

    /// <summary>
    /// Stream wrapper that releases WebDAV lock on disposal.
    /// </summary>
    internal class WebDavLockReleaseStream : Stream
    {
        private readonly Stream _innerStream;
        private readonly WebDavLock? _lockToken;
        private readonly WebDavStrategy _strategy;
        private readonly string _resourceUrl;
        private bool _disposed;

        public WebDavLockReleaseStream(Stream innerStream, WebDavLock? lockToken, WebDavStrategy strategy, string resourceUrl)
        {
            _innerStream = innerStream;
            _lockToken = lockToken;
            _strategy = strategy;
            _resourceUrl = resourceUrl;
        }

        public override bool CanRead => _innerStream.CanRead;
        public override bool CanSeek => _innerStream.CanSeek;
        public override bool CanWrite => _innerStream.CanWrite;
        public override long Length => _innerStream.Length;
        public override long Position
        {
            get => _innerStream.Position;
            set => _innerStream.Position = value;
        }

        public override void Flush() => _innerStream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _innerStream.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _innerStream.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
        public override void SetLength(long value) => _innerStream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _innerStream.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _innerStream.WriteAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _innerStream?.Dispose();
                    if (_lockToken != null)
                    {
                        // Sync bridge: Dispose cannot be async without IAsyncDisposable. Task.Run avoids deadlocks.
                        Task.Run(() => _strategy.ReleaseLockAsync(_resourceUrl, _lockToken, CancellationToken.None)).ConfigureAwait(false).GetAwaiter().GetResult();
                    }
                }
                _disposed = true;
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                if (_innerStream != null)
                {
                    await _innerStream.DisposeAsync();
                }
                if (_lockToken != null)
                {
                    await _strategy.ReleaseLockAsync(_resourceUrl, _lockToken, CancellationToken.None);
                }
                _disposed = true;
            }
            await base.DisposeAsync();
        }
    }

    #endregion
}
