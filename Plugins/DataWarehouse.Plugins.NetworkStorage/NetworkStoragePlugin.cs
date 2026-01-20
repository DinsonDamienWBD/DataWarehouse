using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Infrastructure;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Storage;
using DataWarehouse.SDK.Utilities;
using System.Net;

namespace DataWarehouse.Plugins.NetworkStorage
{
    /// <summary>
    /// Network storage plugin supporting various network storage types.
    ///
    /// Supported network types:
    /// - UNC paths (\\server\share)
    /// - Mapped drives (Z:\)
    /// - SMB/CIFS shares
    /// - IP-based paths
    /// - URI/URL based paths (http, https, ftp)
    /// - WebDAV
    ///
    /// Features:
    /// - Automatic protocol detection
    /// - Connection pooling and retry logic
    /// - Credential management
    /// - Automatic reconnection on failure
    /// - Support for authenticated and anonymous access
    /// - Chunked transfer for large files
    /// - Multi-instance support with role-based selection
    /// - TTL-based caching support
    /// - Document indexing and search
    ///
    /// Message Commands:
    /// - storage.network.save: Save data to network storage
    /// - storage.network.load: Load data from network storage
    /// - storage.network.delete: Delete data from network storage
    /// - storage.network.exists: Check if resource exists
    /// - storage.network.list: List resources
    /// - storage.network.ping: Test network connectivity
    /// - storage.instance.*: Multi-instance management
    /// </summary>
    public sealed class NetworkStoragePlugin : HybridStoragePluginBase<NetworkStorageConfig>
    {
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private NetworkCredential? _credential;
        private bool _isConnected;

        public override string Id => "datawarehouse.plugins.storage.network";
        public override string Name => "Network Storage";
        public override string Version => "2.0.0";
        public override string Scheme => "network";
        public override string StorageCategory => "Network";

        /// <summary>
        /// Creates a network storage plugin with optional configuration.
        /// </summary>
        public NetworkStoragePlugin(NetworkStorageConfig? config = null)
            : base(config ?? new NetworkStorageConfig())
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            };

            if (_config.Credentials != null)
            {
                _credential = _config.Credentials;
                handler.Credentials = _credential;
            }

            // Security Warning: AcceptAnyCertificate bypasses SSL/TLS validation.
            // This should ONLY be used in development/testing environments.
            if (_config.AcceptAnyCertificate)
            {
                if (!_config.IsDevelopmentMode)
                {
                    throw new InvalidOperationException(
                        "Security Error: AcceptAnyCertificate requires IsDevelopmentMode=true. " +
                        "Disabling certificate validation in production is a critical security vulnerability.");
                }

                // Log security warning
                Console.Error.WriteLine(
                    "[SECURITY WARNING] SSL certificate validation is DISABLED. " +
                    "This is vulnerable to man-in-the-middle attacks. " +
                    "Only use in development environments.");

                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            }

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
            };
        }

        /// <summary>
        /// Creates a connection for the given configuration.
        /// </summary>
        protected override Task<object> CreateConnectionAsync(NetworkStorageConfig config)
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            };

            if (config.Credentials != null)
            {
                handler.Credentials = config.Credentials;
            }

            if (config.AcceptAnyCertificate && config.IsDevelopmentMode)
            {
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            }

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            };

            return Task.FromResult<object>(new NetworkStorageConnection
            {
                Config = config,
                HttpClient = client,
                Credential = config.Credentials
            });
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            var capabilities = base.GetCapabilities();
            capabilities.AddRange(new[]
            {
                new PluginCapabilityDescriptor { Name = "storage.network.save", DisplayName = "Save", Description = "Store data to network location" },
                new PluginCapabilityDescriptor { Name = "storage.network.load", DisplayName = "Load", Description = "Retrieve data from network location" },
                new PluginCapabilityDescriptor { Name = "storage.network.delete", DisplayName = "Delete", Description = "Remove data from network location" },
                new PluginCapabilityDescriptor { Name = "storage.network.exists", DisplayName = "Exists", Description = "Check if network resource exists" },
                new PluginCapabilityDescriptor { Name = "storage.network.list", DisplayName = "List", Description = "List network resources" },
                new PluginCapabilityDescriptor { Name = "storage.network.ping", DisplayName = "Ping", Description = "Test network connectivity" },
                new PluginCapabilityDescriptor { Name = "storage.network.connect", DisplayName = "Connect", Description = "Establish network connection" },
                new PluginCapabilityDescriptor { Name = "storage.network.disconnect", DisplayName = "Disconnect", Description = "Close network connection" }
            });
            return capabilities;
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Description"] = "Network storage supporting UNC, SMB, HTTP, FTP, and WebDAV.";
            metadata["BaseUri"] = _config.BaseUri?.ToString() ?? "dynamic";
            metadata["SupportedProtocols"] = new[] { "smb", "unc", "http", "https", "ftp", "webdav" };
            metadata["RetryEnabled"] = _config.EnableRetry;
            metadata["ChunkedTransfer"] = _config.EnableChunkedTransfer;
            metadata["SupportsConcurrency"] = true;
            metadata["SupportsListing"] = true;
            return metadata;
        }

        /// <summary>
        /// Handles incoming messages for this plugin.
        /// </summary>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            var response = message.Type switch
            {
                "storage.network.save" => await HandleSaveAsync(message),
                "storage.network.load" => await HandleLoadAsync(message),
                "storage.network.delete" => await HandleDeleteAsync(message),
                "storage.network.exists" => await HandleExistsAsync(message),
                "storage.network.ping" => await HandlePingAsync(message),
                "storage.network.connect" => await HandleConnectAsync(message),
                "storage.network.disconnect" => HandleDisconnect(message),
                _ => null
            };

            // If not handled, delegate to base for multi-instance management
            if (response == null)
            {
                await base.OnMessageAsync(message);
            }
        }

        private async Task<MessageResponse> HandleSaveAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("uri", out var uriObj) ||
                !payload.TryGetValue("data", out var dataObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'uri' and 'data'");
            }

            var uri = uriObj is Uri u ? u : new Uri(uriObj.ToString()!);
            var data = dataObj switch
            {
                Stream s => s,
                byte[] b => new MemoryStream(b),
                string str => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(str)),
                _ => throw new ArgumentException("Data must be Stream, byte[], or string")
            };

            // Optional: get instanceId for multi-instance support
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            await SaveAsync(uri, data, instanceId);
            return MessageResponse.Ok(new { Uri = uri.ToString(), Success = true, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleLoadAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("uri", out var uriObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'uri'");
            }

            var uri = uriObj is Uri u ? u : new Uri(uriObj.ToString()!);
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var stream = await LoadAsync(uri, instanceId);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return MessageResponse.Ok(new { Uri = uri.ToString(), Data = ms.ToArray(), InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleDeleteAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("uri", out var uriObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'uri'");
            }

            var uri = uriObj is Uri u ? u : new Uri(uriObj.ToString()!);
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            await DeleteAsync(uri, instanceId);
            return MessageResponse.Ok(new { Uri = uri.ToString(), Deleted = true, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleExistsAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("uri", out var uriObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'uri'");
            }

            var uri = uriObj is Uri u ? u : new Uri(uriObj.ToString()!);
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var exists = await ExistsAsync(uri, instanceId);
            return MessageResponse.Ok(new { Uri = uri.ToString(), Exists = exists, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandlePingAsync(PluginMessage message)
        {
            var instanceId = message.Payload is Dictionary<string, object> payload &&
                payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var config = GetConfig(instanceId);
            var targetUri = config.BaseUri;

            if (message.Payload is Dictionary<string, object> p &&
                p.TryGetValue("uri", out var uriObj))
            {
                targetUri = uriObj is Uri u ? u : new Uri(uriObj.ToString()!);
            }

            if (targetUri == null)
                return MessageResponse.Error("No target URI specified");

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var isReachable = await PingAsync(targetUri, instanceId);
                sw.Stop();

                return MessageResponse.Ok(new
                {
                    Uri = targetUri.ToString(),
                    Reachable = isReachable,
                    LatencyMs = sw.ElapsedMilliseconds,
                    InstanceId = instanceId
                });
            }
            catch (Exception ex)
            {
                return MessageResponse.Error($"Ping failed: {ex.Message}");
            }
        }

        private async Task<MessageResponse> HandleConnectAsync(PluginMessage message)
        {
            if (message.Payload is Dictionary<string, object> payload)
            {
                if (payload.TryGetValue("username", out var userObj) &&
                    payload.TryGetValue("password", out var passObj))
                {
                    var domain = payload.TryGetValue("domain", out var domainObj) ? domainObj?.ToString() : null;
                    _credential = new NetworkCredential(userObj?.ToString(), passObj?.ToString(), domain);
                }
            }

            await EnsureConnectedAsync();
            return MessageResponse.Ok(new { Connected = _isConnected });
        }

        private MessageResponse HandleDisconnect(PluginMessage message)
        {
            _isConnected = false;
            return MessageResponse.Ok(new { Disconnected = true });
        }

        #region Storage Operations with Instance Support

        /// <summary>
        /// Save data to network storage, optionally targeting a specific instance.
        /// </summary>
        public async Task SaveAsync(Uri uri, Stream data, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);
            ArgumentNullException.ThrowIfNull(data);

            var config = GetConfig(instanceId);
            var resolvedUri = ResolveUri(uri, config);
            var networkType = DetectNetworkType(resolvedUri);

            await ExecuteWithRetryAsync(async () =>
            {
                switch (networkType)
                {
                    case NetworkType.UNC:
                    case NetworkType.MappedDrive:
                    case NetworkType.SMB:
                        await SaveToFileSystemAsync(resolvedUri, data);
                        break;
                    case NetworkType.HTTP:
                    case NetworkType.HTTPS:
                    case NetworkType.WebDAV:
                        await SaveToHttpAsync(resolvedUri, data, instanceId);
                        break;
                    case NetworkType.FTP:
                        await SaveToFtpAsync(resolvedUri, data, instanceId);
                        break;
                    default:
                        throw new NotSupportedException($"Network type not supported: {networkType}");
                }
            }, config);

            // Auto-index if enabled
            if (config.EnableIndexing)
            {
                await IndexDocumentAsync(uri.ToString(), new Dictionary<string, object>
                {
                    ["uri"] = uri.ToString(),
                    ["resolvedUri"] = resolvedUri.ToString(),
                    ["networkType"] = networkType.ToString(),
                    ["instanceId"] = instanceId ?? "default"
                });
            }
        }

        public override Task SaveAsync(Uri uri, Stream data) => SaveAsync(uri, data, null);

        /// <summary>
        /// Load data from network storage, optionally from a specific instance.
        /// </summary>
        public async Task<Stream> LoadAsync(Uri uri, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var config = GetConfig(instanceId);
            var resolvedUri = ResolveUri(uri, config);
            var networkType = DetectNetworkType(resolvedUri);

            var stream = await ExecuteWithRetryAsync(async () =>
            {
                return networkType switch
                {
                    NetworkType.UNC or NetworkType.MappedDrive or NetworkType.SMB => await LoadFromFileSystemAsync(resolvedUri),
                    NetworkType.HTTP or NetworkType.HTTPS or NetworkType.WebDAV => await LoadFromHttpAsync(resolvedUri, instanceId),
                    NetworkType.FTP => await LoadFromFtpAsync(resolvedUri, instanceId),
                    _ => throw new NotSupportedException($"Network type not supported: {networkType}")
                };
            }, config);

            // Update last access for caching
            await TouchAsync(uri);

            return stream;
        }

        public override Task<Stream> LoadAsync(Uri uri) => LoadAsync(uri, null);

        /// <summary>
        /// Delete data from network storage, optionally from a specific instance.
        /// </summary>
        public async Task DeleteAsync(Uri uri, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var config = GetConfig(instanceId);
            var resolvedUri = ResolveUri(uri, config);
            var networkType = DetectNetworkType(resolvedUri);

            await ExecuteWithRetryAsync(async () =>
            {
                switch (networkType)
                {
                    case NetworkType.UNC:
                    case NetworkType.MappedDrive:
                    case NetworkType.SMB:
                        await DeleteFromFileSystemAsync(resolvedUri);
                        break;
                    case NetworkType.HTTP:
                    case NetworkType.HTTPS:
                    case NetworkType.WebDAV:
                        await DeleteFromHttpAsync(resolvedUri, instanceId);
                        break;
                    case NetworkType.FTP:
                        await DeleteFromFtpAsync(resolvedUri, instanceId);
                        break;
                    default:
                        throw new NotSupportedException($"Network type not supported: {networkType}");
                }
            }, config);

            // Remove from index
            _ = RemoveFromIndexAsync(uri.ToString());
        }

        public override Task DeleteAsync(Uri uri) => DeleteAsync(uri, null);

        /// <summary>
        /// Check if data exists in network storage, optionally in a specific instance.
        /// </summary>
        public async Task<bool> ExistsAsync(Uri uri, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var config = GetConfig(instanceId);
            var resolvedUri = ResolveUri(uri, config);
            var networkType = DetectNetworkType(resolvedUri);

            try
            {
                return await ExecuteWithRetryAsync(async () =>
                {
                    return networkType switch
                    {
                        NetworkType.UNC or NetworkType.MappedDrive or NetworkType.SMB => File.Exists(GetLocalPath(resolvedUri)),
                        NetworkType.HTTP or NetworkType.HTTPS or NetworkType.WebDAV => await ExistsHttpAsync(resolvedUri, instanceId),
                        NetworkType.FTP => await ExistsFtpAsync(resolvedUri, instanceId),
                        _ => false
                    };
                }, config);
            }
            catch
            {
                return false;
            }
        }

        public override Task<bool> ExistsAsync(Uri uri) => ExistsAsync(uri, null);

        public override async IAsyncEnumerable<StorageListItem> ListFilesAsync(
            string prefix = "",
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var baseUri = _config.BaseUri ?? throw new InvalidOperationException("BaseUri not configured");
            var networkType = DetectNetworkType(baseUri);

            if (networkType is NetworkType.UNC or NetworkType.MappedDrive or NetworkType.SMB)
            {
                var basePath = GetLocalPath(baseUri);
                var searchPath = string.IsNullOrEmpty(prefix) ? basePath : Path.Combine(basePath, prefix);

                if (Directory.Exists(searchPath))
                {
                    var files = Directory.EnumerateFiles(searchPath, "*", SearchOption.AllDirectories);

                    foreach (var file in files)
                    {
                        if (ct.IsCancellationRequested)
                            yield break;

                        var relativePath = Path.GetRelativePath(basePath, file);
                        var uri = new Uri(baseUri, relativePath.Replace(Path.DirectorySeparatorChar, '/'));
                        var fileInfo = new FileInfo(file);
                        yield return new StorageListItem(uri, fileInfo.Length);

                        await Task.Yield();
                    }
                }
            }
            // HTTP/FTP listing would require protocol-specific implementations
        }

        #endregion

        #region Helper Methods

        private NetworkStorageConfig GetConfig(string? instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
                return _config;

            var instance = _connectionRegistry.Get(instanceId);
            return instance?.Config ?? _config;
        }

        private HttpClient GetHttpClient(string? instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
                return _httpClient;

            var instance = _connectionRegistry.Get(instanceId);
            if (instance?.Connection is NetworkStorageConnection conn)
                return conn.HttpClient;

            return _httpClient;
        }

        private NetworkCredential? GetCredential(string? instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
                return _credential;

            var instance = _connectionRegistry.Get(instanceId);
            if (instance?.Connection is NetworkStorageConnection conn)
                return conn.Credential;

            return _credential;
        }

        private async Task<bool> PingAsync(Uri uri, string? instanceId = null)
        {
            var networkType = DetectNetworkType(uri);

            if (networkType is NetworkType.HTTP or NetworkType.HTTPS)
            {
                try
                {
                    var client = GetHttpClient(instanceId);
                    using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri));
                    return response.IsSuccessStatusCode;
                }
                catch
                {
                    return false;
                }
            }
            else if (networkType is NetworkType.UNC or NetworkType.MappedDrive or NetworkType.SMB)
            {
                var path = GetLocalPath(uri);
                return Directory.Exists(Path.GetDirectoryName(path)) || File.Exists(path);
            }

            return false;
        }

        private async Task EnsureConnectedAsync()
        {
            if (_isConnected) return;

            await _connectionLock.WaitAsync();
            try
            {
                if (_isConnected) return;

                if (_config.BaseUri != null)
                {
                    _isConnected = await PingAsync(_config.BaseUri);
                }
                else
                {
                    _isConnected = true;
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private Uri ResolveUri(Uri uri, NetworkStorageConfig config)
        {
            if (uri.IsAbsoluteUri)
                return uri;

            if (config.BaseUri != null)
                return new Uri(config.BaseUri, uri);

            return uri;
        }

        private static NetworkType DetectNetworkType(Uri uri)
        {
            // Check for UNC path
            if (uri.IsUnc || uri.LocalPath.StartsWith(@"\\"))
                return NetworkType.UNC;

            // Check scheme
            return uri.Scheme.ToLowerInvariant() switch
            {
                "file" when uri.LocalPath.StartsWith(@"\\") => NetworkType.UNC,
                "file" when char.IsLetter(uri.LocalPath.TrimStart('/')[0]) => NetworkType.MappedDrive,
                "smb" => NetworkType.SMB,
                "http" => NetworkType.HTTP,
                "https" => NetworkType.HTTPS,
                "ftp" or "ftps" => NetworkType.FTP,
                "webdav" or "davs" => NetworkType.WebDAV,
                _ => NetworkType.Unknown
            };
        }

        private static string GetLocalPath(Uri uri)
        {
            if (uri.IsUnc)
                return uri.LocalPath;

            if (uri.Scheme == "file")
                return uri.LocalPath;

            // SMB scheme
            if (uri.Scheme == "smb")
                return $@"\\{uri.Host}\{uri.AbsolutePath.TrimStart('/')}".Replace('/', '\\');

            return uri.LocalPath;
        }

        private async Task SaveToFileSystemAsync(Uri uri, Stream data)
        {
            var path = GetLocalPath(uri);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await data.CopyToAsync(fs);
        }

        private async Task<Stream> LoadFromFileSystemAsync(Uri uri)
        {
            var path = GetLocalPath(uri);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Network file not found: {path}");

            var ms = new MemoryStream();
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            await fs.CopyToAsync(ms);
            ms.Position = 0;
            return ms;
        }

        private Task DeleteFromFileSystemAsync(Uri uri)
        {
            var path = GetLocalPath(uri);
            if (File.Exists(path))
                File.Delete(path);
            return Task.CompletedTask;
        }

        private async Task SaveToHttpAsync(Uri uri, Stream data, string? instanceId = null)
        {
            var client = GetHttpClient(instanceId);
            using var content = new StreamContent(data);
            using var response = await client.PutAsync(uri, content);
            response.EnsureSuccessStatusCode();
        }

        private async Task<Stream> LoadFromHttpAsync(Uri uri, string? instanceId = null)
        {
            var client = GetHttpClient(instanceId);
            var response = await client.GetAsync(uri);
            response.EnsureSuccessStatusCode();
            var ms = new MemoryStream();
            await response.Content.CopyToAsync(ms);
            ms.Position = 0;
            return ms;
        }

        private async Task DeleteFromHttpAsync(Uri uri, string? instanceId = null)
        {
            var client = GetHttpClient(instanceId);
            using var response = await client.DeleteAsync(uri);
            response.EnsureSuccessStatusCode();
        }

        private async Task<bool> ExistsHttpAsync(Uri uri, string? instanceId = null)
        {
            try
            {
                var client = GetHttpClient(instanceId);
                using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri));
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private async Task SaveToFtpAsync(Uri uri, Stream data, string? instanceId = null)
        {
            var credential = GetCredential(instanceId);
            var request = (FtpWebRequest)WebRequest.Create(uri);
            request.Method = WebRequestMethods.Ftp.UploadFile;
            if (credential != null)
                request.Credentials = credential;

            await using var requestStream = await request.GetRequestStreamAsync();
            await data.CopyToAsync(requestStream);

            using var response = (FtpWebResponse)await request.GetResponseAsync();
        }

        private async Task<Stream> LoadFromFtpAsync(Uri uri, string? instanceId = null)
        {
            var credential = GetCredential(instanceId);
            var request = (FtpWebRequest)WebRequest.Create(uri);
            request.Method = WebRequestMethods.Ftp.DownloadFile;
            if (credential != null)
                request.Credentials = credential;

            using var response = (FtpWebResponse)await request.GetResponseAsync();
            var ms = new MemoryStream();
            await using var responseStream = response.GetResponseStream();
            await responseStream.CopyToAsync(ms);
            ms.Position = 0;
            return ms;
        }

        private async Task DeleteFromFtpAsync(Uri uri, string? instanceId = null)
        {
            var credential = GetCredential(instanceId);
            var request = (FtpWebRequest)WebRequest.Create(uri);
            request.Method = WebRequestMethods.Ftp.DeleteFile;
            if (credential != null)
                request.Credentials = credential;

            using var response = (FtpWebResponse)await request.GetResponseAsync();
        }

        private async Task<bool> ExistsFtpAsync(Uri uri, string? instanceId = null)
        {
            try
            {
                var credential = GetCredential(instanceId);
                var request = (FtpWebRequest)WebRequest.Create(uri);
                request.Method = WebRequestMethods.Ftp.GetFileSize;
                if (credential != null)
                    request.Credentials = credential;

                using var response = (FtpWebResponse)await request.GetResponseAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task ExecuteWithRetryAsync(Func<Task> action, NetworkStorageConfig config)
        {
            var retries = config.EnableRetry ? config.MaxRetries : 0;
            var delay = config.RetryDelayMs;

            for (var attempt = 0; attempt <= retries; attempt++)
            {
                try
                {
                    await action();
                    return;
                }
                catch when (attempt < retries)
                {
                    await Task.Delay(delay);
                    delay *= 2; // Exponential backoff
                }
            }
        }

        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, NetworkStorageConfig config)
        {
            var retries = config.EnableRetry ? config.MaxRetries : 0;
            var delay = config.RetryDelayMs;

            for (var attempt = 0; attempt <= retries; attempt++)
            {
                try
                {
                    return await action();
                }
                catch when (attempt < retries)
                {
                    await Task.Delay(delay);
                    delay *= 2;
                }
            }

            return await action(); // Final attempt - let exception propagate
        }

        #endregion
    }

    /// <summary>
    /// Internal connection wrapper for network storage instances.
    /// </summary>
    internal class NetworkStorageConnection : IDisposable
    {
        public required NetworkStorageConfig Config { get; init; }
        public required HttpClient HttpClient { get; init; }
        public NetworkCredential? Credential { get; init; }

        public void Dispose()
        {
            HttpClient?.Dispose();
        }
    }

    /// <summary>
    /// Configuration for network storage.
    /// </summary>
    public class NetworkStorageConfig : StorageConfigBase
    {
        /// <summary>
        /// Base URI for network storage.
        /// </summary>
        public Uri? BaseUri { get; set; }

        /// <summary>
        /// Network credentials for authenticated access.
        /// </summary>
        public NetworkCredential? Credentials { get; set; }

        /// <summary>
        /// Connection timeout in seconds.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Enable automatic retry on failure.
        /// </summary>
        public bool EnableRetry { get; set; } = true;

        /// <summary>
        /// Enable chunked transfer for large files.
        /// </summary>
        public bool EnableChunkedTransfer { get; set; } = true;

        /// <summary>
        /// Accept any SSL certificate (for testing only).
        /// SECURITY WARNING: Must also set IsDevelopmentMode=true.
        /// </summary>
        public bool AcceptAnyCertificate { get; set; }

        /// <summary>
        /// Indicates this is a development/testing environment.
        /// Required to use AcceptAnyCertificate.
        /// </summary>
        public bool IsDevelopmentMode { get; set; }

        /// <summary>
        /// Creates configuration for UNC path.
        /// </summary>
        public static NetworkStorageConfig ForUnc(string uncPath, string? username = null, string? password = null, string? domain = null)
        {
            return new NetworkStorageConfig
            {
                BaseUri = new Uri(uncPath),
                Credentials = username != null ? new NetworkCredential(username, password, domain) : null
            };
        }

        /// <summary>
        /// Creates configuration for UNC path with instance ID.
        /// </summary>
        public static NetworkStorageConfig ForUnc(string uncPath, string instanceId, string? username = null, string? password = null, string? domain = null)
        {
            return new NetworkStorageConfig
            {
                BaseUri = new Uri(uncPath),
                Credentials = username != null ? new NetworkCredential(username, password, domain) : null,
                InstanceId = instanceId
            };
        }

        /// <summary>
        /// Creates configuration for HTTP/HTTPS endpoint.
        /// </summary>
        public static NetworkStorageConfig ForHttp(string url, string? username = null, string? password = null)
        {
            return new NetworkStorageConfig
            {
                BaseUri = new Uri(url),
                Credentials = username != null ? new NetworkCredential(username, password) : null
            };
        }

        /// <summary>
        /// Creates configuration for HTTP/HTTPS endpoint with instance ID.
        /// </summary>
        public static NetworkStorageConfig ForHttp(string url, string instanceId, string? username = null, string? password = null)
        {
            return new NetworkStorageConfig
            {
                BaseUri = new Uri(url),
                Credentials = username != null ? new NetworkCredential(username, password) : null,
                InstanceId = instanceId
            };
        }

        /// <summary>
        /// Creates configuration for FTP server.
        /// </summary>
        public static NetworkStorageConfig ForFtp(string host, string? username = null, string? password = null)
        {
            return new NetworkStorageConfig
            {
                BaseUri = new Uri($"ftp://{host}"),
                Credentials = username != null ? new NetworkCredential(username, password) : null
            };
        }

        /// <summary>
        /// Creates configuration for FTP server with instance ID.
        /// </summary>
        public static NetworkStorageConfig ForFtp(string host, string instanceId, string? username = null, string? password = null)
        {
            return new NetworkStorageConfig
            {
                BaseUri = new Uri($"ftp://{host}"),
                Credentials = username != null ? new NetworkCredential(username, password) : null,
                InstanceId = instanceId
            };
        }
    }

    /// <summary>
    /// Network storage type.
    /// </summary>
    public enum NetworkType
    {
        Unknown,
        UNC,
        MappedDrive,
        SMB,
        HTTP,
        HTTPS,
        FTP,
        WebDAV
    }
}
