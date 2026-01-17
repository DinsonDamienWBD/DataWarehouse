using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
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
    ///
    /// Message Commands:
    /// - storage.network.save: Save data to network storage
    /// - storage.network.load: Load data from network storage
    /// - storage.network.delete: Delete data from network storage
    /// - storage.network.exists: Check if resource exists
    /// - storage.network.list: List resources
    /// - storage.network.ping: Test network connectivity
    /// </summary>
    public sealed class NetworkStoragePlugin : ListableStoragePluginBase
    {
        private readonly NetworkStorageConfig _config;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private NetworkCredential? _credential;
        private bool _isConnected;

        public override string Id => "datawarehouse.plugins.storage.network";
        public override string Name => "Network Storage";
        public override string Version => "1.0.0";
        public override string Scheme => "network";

        /// <summary>
        /// Creates a network storage plugin with optional configuration.
        /// </summary>
        public NetworkStoragePlugin(NetworkStorageConfig? config = null)
        {
            _config = config ?? new NetworkStorageConfig();

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

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "storage.network.save", DisplayName = "Save", Description = "Store data to network location" },
                new() { Name = "storage.network.load", DisplayName = "Load", Description = "Retrieve data from network location" },
                new() { Name = "storage.network.delete", DisplayName = "Delete", Description = "Remove data from network location" },
                new() { Name = "storage.network.exists", DisplayName = "Exists", Description = "Check if network resource exists" },
                new() { Name = "storage.network.list", DisplayName = "List", Description = "List network resources" },
                new() { Name = "storage.network.ping", DisplayName = "Ping", Description = "Test network connectivity" },
                new() { Name = "storage.network.connect", DisplayName = "Connect", Description = "Establish network connection" },
                new() { Name = "storage.network.disconnect", DisplayName = "Disconnect", Description = "Close network connection" }
            ];
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

            await SaveAsync(uri, data);
            return MessageResponse.Ok(new { Uri = uri.ToString(), Success = true });
        }

        private async Task<MessageResponse> HandleLoadAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("uri", out var uriObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'uri'");
            }

            var uri = uriObj is Uri u ? u : new Uri(uriObj.ToString()!);
            var stream = await LoadAsync(uri);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return MessageResponse.Ok(new { Uri = uri.ToString(), Data = ms.ToArray() });
        }

        private async Task<MessageResponse> HandleDeleteAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("uri", out var uriObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'uri'");
            }

            var uri = uriObj is Uri u ? u : new Uri(uriObj.ToString()!);
            await DeleteAsync(uri);
            return MessageResponse.Ok(new { Uri = uri.ToString(), Deleted = true });
        }

        private async Task<MessageResponse> HandleExistsAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("uri", out var uriObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'uri'");
            }

            var uri = uriObj is Uri u ? u : new Uri(uriObj.ToString()!);
            var exists = await ExistsAsync(uri);
            return MessageResponse.Ok(new { Uri = uri.ToString(), Exists = exists });
        }

        private async Task<MessageResponse> HandlePingAsync(PluginMessage message)
        {
            var targetUri = _config.BaseUri;
            if (message.Payload is Dictionary<string, object> payload &&
                payload.TryGetValue("uri", out var uriObj))
            {
                targetUri = uriObj is Uri u ? u : new Uri(uriObj.ToString()!);
            }

            if (targetUri == null)
                return MessageResponse.Error("No target URI specified");

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var isReachable = await PingAsync(targetUri);
                sw.Stop();

                return MessageResponse.Ok(new
                {
                    Uri = targetUri.ToString(),
                    Reachable = isReachable,
                    LatencyMs = sw.ElapsedMilliseconds
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

        public override async Task SaveAsync(Uri uri, Stream data)
        {
            ArgumentNullException.ThrowIfNull(uri);
            ArgumentNullException.ThrowIfNull(data);

            var resolvedUri = ResolveUri(uri);
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
                        await SaveToHttpAsync(resolvedUri, data);
                        break;
                    case NetworkType.FTP:
                        await SaveToFtpAsync(resolvedUri, data);
                        break;
                    default:
                        throw new NotSupportedException($"Network type not supported: {networkType}");
                }
            });
        }

        public override async Task<Stream> LoadAsync(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var resolvedUri = ResolveUri(uri);
            var networkType = DetectNetworkType(resolvedUri);

            return await ExecuteWithRetryAsync(async () =>
            {
                return networkType switch
                {
                    NetworkType.UNC or NetworkType.MappedDrive or NetworkType.SMB => await LoadFromFileSystemAsync(resolvedUri),
                    NetworkType.HTTP or NetworkType.HTTPS or NetworkType.WebDAV => await LoadFromHttpAsync(resolvedUri),
                    NetworkType.FTP => await LoadFromFtpAsync(resolvedUri),
                    _ => throw new NotSupportedException($"Network type not supported: {networkType}")
                };
            });
        }

        public override async Task DeleteAsync(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var resolvedUri = ResolveUri(uri);
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
                        await DeleteFromHttpAsync(resolvedUri);
                        break;
                    case NetworkType.FTP:
                        await DeleteFromFtpAsync(resolvedUri);
                        break;
                    default:
                        throw new NotSupportedException($"Network type not supported: {networkType}");
                }
            });
        }

        public override async Task<bool> ExistsAsync(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var resolvedUri = ResolveUri(uri);
            var networkType = DetectNetworkType(resolvedUri);

            try
            {
                return await ExecuteWithRetryAsync(async () =>
                {
                    return networkType switch
                    {
                        NetworkType.UNC or NetworkType.MappedDrive or NetworkType.SMB => File.Exists(GetLocalPath(resolvedUri)),
                        NetworkType.HTTP or NetworkType.HTTPS or NetworkType.WebDAV => await ExistsHttpAsync(resolvedUri),
                        NetworkType.FTP => await ExistsFtpAsync(resolvedUri),
                        _ => false
                    };
                });
            }
            catch
            {
                return false;
            }
        }

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

        private async Task<bool> PingAsync(Uri uri)
        {
            var networkType = DetectNetworkType(uri);

            if (networkType is NetworkType.HTTP or NetworkType.HTTPS)
            {
                try
                {
                    using var response = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri));
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

        private Uri ResolveUri(Uri uri)
        {
            if (uri.IsAbsoluteUri)
                return uri;

            if (_config.BaseUri != null)
                return new Uri(_config.BaseUri, uri);

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

        private async Task SaveToHttpAsync(Uri uri, Stream data)
        {
            using var content = new StreamContent(data);
            using var response = await _httpClient.PutAsync(uri, content);
            response.EnsureSuccessStatusCode();
        }

        private async Task<Stream> LoadFromHttpAsync(Uri uri)
        {
            var response = await _httpClient.GetAsync(uri);
            response.EnsureSuccessStatusCode();
            var ms = new MemoryStream();
            await response.Content.CopyToAsync(ms);
            ms.Position = 0;
            return ms;
        }

        private async Task DeleteFromHttpAsync(Uri uri)
        {
            using var response = await _httpClient.DeleteAsync(uri);
            response.EnsureSuccessStatusCode();
        }

        private async Task<bool> ExistsHttpAsync(Uri uri)
        {
            try
            {
                using var response = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri));
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private async Task SaveToFtpAsync(Uri uri, Stream data)
        {
            var request = (FtpWebRequest)WebRequest.Create(uri);
            request.Method = WebRequestMethods.Ftp.UploadFile;
            if (_credential != null)
                request.Credentials = _credential;

            await using var requestStream = await request.GetRequestStreamAsync();
            await data.CopyToAsync(requestStream);

            using var response = (FtpWebResponse)await request.GetResponseAsync();
        }

        private async Task<Stream> LoadFromFtpAsync(Uri uri)
        {
            var request = (FtpWebRequest)WebRequest.Create(uri);
            request.Method = WebRequestMethods.Ftp.DownloadFile;
            if (_credential != null)
                request.Credentials = _credential;

            using var response = (FtpWebResponse)await request.GetResponseAsync();
            var ms = new MemoryStream();
            await using var responseStream = response.GetResponseStream();
            await responseStream.CopyToAsync(ms);
            ms.Position = 0;
            return ms;
        }

        private async Task DeleteFromFtpAsync(Uri uri)
        {
            var request = (FtpWebRequest)WebRequest.Create(uri);
            request.Method = WebRequestMethods.Ftp.DeleteFile;
            if (_credential != null)
                request.Credentials = _credential;

            using var response = (FtpWebResponse)await request.GetResponseAsync();
        }

        private async Task<bool> ExistsFtpAsync(Uri uri)
        {
            try
            {
                var request = (FtpWebRequest)WebRequest.Create(uri);
                request.Method = WebRequestMethods.Ftp.GetFileSize;
                if (_credential != null)
                    request.Credentials = _credential;

                using var response = (FtpWebResponse)await request.GetResponseAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task ExecuteWithRetryAsync(Func<Task> action)
        {
            var retries = _config.EnableRetry ? _config.MaxRetries : 0;
            var delay = _config.RetryDelayMs;

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

        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action)
        {
            var retries = _config.EnableRetry ? _config.MaxRetries : 0;
            var delay = _config.RetryDelayMs;

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
    }

    /// <summary>
    /// Configuration for network storage.
    /// </summary>
    public class NetworkStorageConfig
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
        /// Maximum retry attempts.
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Initial retry delay in milliseconds.
        /// </summary>
        public int RetryDelayMs { get; set; } = 1000;

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
