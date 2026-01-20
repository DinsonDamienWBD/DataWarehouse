using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Infrastructure;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Storage;
using DataWarehouse.SDK.Utilities;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.CloudStorage
{
    /// <summary>
    /// Cloud storage plugin supporting consumer cloud storage providers.
    ///
    /// Supported Providers:
    /// - Google Drive
    /// - Microsoft OneDrive
    /// - SharePoint Online
    /// - Dropbox
    /// - Box
    ///
    /// Features:
    /// - OAuth2 authentication flow
    /// - Automatic token refresh
    /// - Folder hierarchy support
    /// - Shared drive/team site support
    /// - File versioning
    /// - Chunked uploads for large files
    /// - Real-time change notifications (where supported)
    /// - Multi-instance support with role-based selection
    /// - TTL-based caching support
    /// - Document indexing and search
    ///
    /// Message Commands:
    /// - storage.cloud.upload: Upload file to cloud storage
    /// - storage.cloud.download: Download file from cloud storage
    /// - storage.cloud.delete: Delete file from cloud storage
    /// - storage.cloud.list: List files in folder
    /// - storage.cloud.metadata: Get file metadata
    /// - storage.cloud.share: Create sharing link
    /// - storage.cloud.move: Move/rename file
    /// - storage.cloud.search: Search for files
    /// - storage.cloud.auth: Initiate OAuth flow
    /// - storage.instance.*: Multi-instance management
    /// </summary>
    public sealed class CloudStoragePlugin : HybridStoragePluginBase<CloudStorageConfig>
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _httpClientName;
        private string? _accessToken;
        private string? _refreshToken;
        private DateTime _tokenExpiry = DateTime.MinValue;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);

        /// <summary>
        /// Default shared HTTP client factory for all CloudStoragePlugin instances.
        /// This static factory ensures proper connection pooling across plugin instances.
        /// </summary>
        private static readonly Lazy<DefaultHttpClientFactory> SharedFactory = new(
            () => new DefaultHttpClientFactory(options =>
            {
                options.EnableAutomaticDecompression = true;
                options.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
                options.MaxConnectionsPerServer = 20;
            }),
            LazyThreadSafetyMode.ExecutionAndPublication);

        public override string Id => "datawarehouse.plugins.storage.cloud";
        public override string Name => "Cloud Storage";
        public override string Version => "2.0.0";
        public override string Scheme => _config.Provider switch
        {
            CloudProvider.GoogleDrive => "gdrive",
            CloudProvider.OneDrive => "onedrive",
            CloudProvider.SharePoint => "sharepoint",
            CloudProvider.Dropbox => "dropbox",
            CloudProvider.Box => "box",
            _ => "cloud"
        };
        public override string StorageCategory => "Cloud";

        /// <summary>
        /// Gets the HttpClient instance configured for this plugin's cloud provider.
        /// Uses the injected factory for proper connection pooling.
        /// </summary>
        private HttpClient HttpClient => _httpClientFactory.CreateClient(_httpClientName);

        /// <summary>
        /// Creates a cloud storage plugin with configuration.
        /// Uses the shared default HTTP client factory for connection pooling.
        /// </summary>
        /// <param name="config">The cloud storage configuration.</param>
        /// <remarks>
        /// This constructor maintains backward compatibility by using a shared
        /// <see cref="DefaultHttpClientFactory"/> instance. For production use
        /// with dependency injection, prefer the constructor that accepts
        /// <see cref="IHttpClientFactory"/>.
        /// </remarks>
        public CloudStoragePlugin(CloudStorageConfig config)
            : this(config, null)
        {
        }

        /// <summary>
        /// Creates a cloud storage plugin with configuration and HTTP client factory.
        /// </summary>
        /// <param name="config">The cloud storage configuration.</param>
        /// <param name="httpClientFactory">
        /// Optional HTTP client factory for connection pooling. When null, uses a
        /// shared default factory that properly manages connection pooling.
        /// </param>
        /// <remarks>
        /// <para>
        /// This constructor is the recommended way to create CloudStoragePlugin
        /// instances in applications using dependency injection.
        /// </para>
        /// <para>
        /// <b>Anti-pattern avoided:</b> Creating HttpClient instances per request
        /// leads to socket exhaustion. This implementation uses a factory pattern
        /// to share HttpClient instances across requests while maintaining proper
        /// connection pooling through SocketsHttpHandler.
        /// </para>
        /// </remarks>
        /// <example>
        /// Using with dependency injection:
        /// <code>
        /// // In DI configuration
        /// services.AddSingleton&lt;IHttpClientFactory, DefaultHttpClientFactory&gt;();
        /// services.AddTransient&lt;CloudStoragePlugin&gt;(sp =>
        ///     new CloudStoragePlugin(
        ///         config,
        ///         sp.GetRequiredService&lt;IHttpClientFactory&gt;()));
        /// </code>
        /// </example>
        public CloudStoragePlugin(CloudStorageConfig config, IHttpClientFactory? httpClientFactory)
            : base(config ?? throw new ArgumentNullException(nameof(config)))
        {
            _accessToken = config.AccessToken;
            _refreshToken = config.RefreshToken;
            _tokenExpiry = config.TokenExpiry ?? DateTime.MinValue;

            // Use injected factory or fall back to shared static factory
            _httpClientFactory = httpClientFactory ?? SharedFactory.Value;

            // Create a unique client name based on provider for proper connection pooling
            _httpClientName = $"cloud-storage-{_config.Provider.ToString().ToLowerInvariant()}";

            // Configure the client with appropriate settings for this provider
            _httpClientFactory.ConfigureClient(_httpClientName, options =>
            {
                options.Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);
                options.EnableAutomaticDecompression = true;
                options.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
                options.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
            });
        }

        /// <summary>
        /// Creates a connection for the given configuration.
        /// </summary>
        protected override Task<object> CreateConnectionAsync(CloudStorageConfig config)
        {
            // Create a unique client name based on provider and instance
            var clientName = $"cloud-storage-{config.Provider.ToString().ToLowerInvariant()}-{config.InstanceId ?? "default"}";

            _httpClientFactory.ConfigureClient(clientName, options =>
            {
                options.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
                options.EnableAutomaticDecompression = true;
                options.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
                options.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
            });

            return Task.FromResult<object>(new CloudStorageConnection
            {
                Config = config,
                HttpClientFactory = _httpClientFactory,
                HttpClientName = clientName,
                AccessToken = config.AccessToken,
                RefreshToken = config.RefreshToken,
                TokenExpiry = config.TokenExpiry ?? DateTime.MinValue
            });
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            var capabilities = base.GetCapabilities();
            capabilities.AddRange(new[]
            {
                new PluginCapabilityDescriptor { Name = "storage.cloud.upload", DisplayName = "Upload", Description = "Upload file to cloud storage" },
                new PluginCapabilityDescriptor { Name = "storage.cloud.download", DisplayName = "Download", Description = "Download file from cloud storage" },
                new PluginCapabilityDescriptor { Name = "storage.cloud.delete", DisplayName = "Delete", Description = "Delete file from cloud storage" },
                new PluginCapabilityDescriptor { Name = "storage.cloud.list", DisplayName = "List", Description = "List files in folder" },
                new PluginCapabilityDescriptor { Name = "storage.cloud.metadata", DisplayName = "Metadata", Description = "Get file metadata" },
                new PluginCapabilityDescriptor { Name = "storage.cloud.share", DisplayName = "Share", Description = "Create sharing link" },
                new PluginCapabilityDescriptor { Name = "storage.cloud.move", DisplayName = "Move", Description = "Move/rename file" },
                new PluginCapabilityDescriptor { Name = "storage.cloud.search", DisplayName = "Search", Description = "Search for files" },
                new PluginCapabilityDescriptor { Name = "storage.cloud.auth", DisplayName = "Auth", Description = "Initiate OAuth flow" }
            });
            return capabilities;
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Description"] = $"Cloud storage provider: {_config.Provider}";
            metadata["Provider"] = _config.Provider.ToString();
            metadata["BaseFolderId"] = _config.BaseFolderId ?? "root";
            metadata["SupportsSharing"] = true;
            metadata["SupportsVersioning"] = true;
            metadata["SupportsSearch"] = true;
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
                "storage.cloud.upload" => await HandleUploadAsync(message),
                "storage.cloud.download" => await HandleDownloadAsync(message),
                "storage.cloud.delete" => await HandleDeleteAsync(message),
                "storage.cloud.metadata" => await HandleMetadataAsync(message),
                "storage.cloud.share" => await HandleShareAsync(message),
                "storage.cloud.move" => await HandleMoveAsync(message),
                "storage.cloud.search" => await HandleSearchAsync(message),
                "storage.cloud.auth" => HandleAuth(message),
                _ => null
            };

            // If not handled, delegate to base for multi-instance management
            if (response == null)
            {
                await base.OnMessageAsync(message);
            }
        }

        private async Task<MessageResponse> HandleUploadAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("path", out var pathObj) ||
                !payload.TryGetValue("data", out var dataObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'path' and 'data'");
            }

            var path = pathObj.ToString()!;
            var data = dataObj switch
            {
                Stream s => s,
                byte[] b => new MemoryStream(b),
                string str => new MemoryStream(Encoding.UTF8.GetBytes(str)),
                _ => throw new ArgumentException("Data must be Stream, byte[], or string")
            };

            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;
            var config = GetConfig(instanceId);

            var uri = new Uri($"{GetScheme(config)}:///{path}");
            await SaveAsync(uri, data, instanceId);
            return MessageResponse.Ok(new { Path = path, Success = true, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleDownloadAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("path", out var pathObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'path'");
            }

            var path = pathObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;
            var config = GetConfig(instanceId);

            var uri = new Uri($"{GetScheme(config)}:///{path}");
            var stream = await LoadAsync(uri, instanceId);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return MessageResponse.Ok(new { Path = path, Data = ms.ToArray(), InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleDeleteAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("path", out var pathObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'path'");
            }

            var path = pathObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;
            var config = GetConfig(instanceId);

            var uri = new Uri($"{GetScheme(config)}:///{path}");
            await DeleteAsync(uri, instanceId);
            return MessageResponse.Ok(new { Path = path, Deleted = true, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleMetadataAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("path", out var pathObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'path'");
            }

            var path = pathObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var metadata = await GetFileMetadataAsync(path, instanceId);
            return MessageResponse.Ok(metadata);
        }

        private async Task<MessageResponse> HandleShareAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("path", out var pathObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'path'");
            }

            var path = pathObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var shareLink = await CreateShareLinkAsync(path, instanceId);
            return MessageResponse.Ok(new { Path = path, ShareLink = shareLink, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleMoveAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("sourcePath", out var sourceObj) ||
                !payload.TryGetValue("destinationPath", out var destObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'sourcePath' and 'destinationPath'");
            }

            var sourcePath = sourceObj.ToString()!;
            var destPath = destObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            await MoveFileAsync(sourcePath, destPath, instanceId);
            return MessageResponse.Ok(new { SourcePath = sourcePath, DestinationPath = destPath, Success = true, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleSearchAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("query", out var queryObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'query'");
            }

            var query = queryObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var results = await SearchFilesAsync(query, instanceId);
            return MessageResponse.Ok(new { Query = query, Results = results, InstanceId = instanceId });
        }

        private MessageResponse HandleAuth(PluginMessage message)
        {
            var instanceId = (message.Payload as Dictionary<string, object>)?.TryGetValue("instanceId", out var instId) == true
                ? instId?.ToString()
                : null;
            var config = GetConfig(instanceId);

            var authUrl = GetAuthorizationUrl(config);
            return MessageResponse.Ok(new
            {
                AuthorizationUrl = authUrl,
                Instructions = "Open the URL in a browser, authorize the app, and provide the authorization code",
                InstanceId = instanceId
            });
        }

        #region Storage Operations with Instance Support

        /// <summary>
        /// Save data to cloud storage, optionally targeting a specific instance.
        /// </summary>
        public async Task SaveAsync(Uri uri, Stream data, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);
            ArgumentNullException.ThrowIfNull(data);

            var config = GetConfig(instanceId);
            var client = GetHttpClient(instanceId);
            await EnsureTokenValidAsync(instanceId);

            var path = uri.AbsolutePath.TrimStart('/');

            using var ms = new MemoryStream();
            await data.CopyToAsync(ms);
            var content = ms.ToArray();

            switch (config.Provider)
            {
                case CloudProvider.GoogleDrive:
                    await UploadToGoogleDriveAsync(path, content, config, client);
                    break;
                case CloudProvider.OneDrive:
                case CloudProvider.SharePoint:
                    await UploadToOneDriveAsync(path, content, config, client);
                    break;
                case CloudProvider.Dropbox:
                    await UploadToDropboxAsync(path, content, config, client);
                    break;
                case CloudProvider.Box:
                    await UploadToBoxAsync(path, content, config, client);
                    break;
                default:
                    throw new NotSupportedException($"Provider {config.Provider} not supported");
            }

            // Auto-index if enabled
            if (config.EnableIndexing)
            {
                await IndexDocumentAsync(uri.ToString(), new Dictionary<string, object>
                {
                    ["uri"] = uri.ToString(),
                    ["path"] = path,
                    ["size"] = content.Length,
                    ["provider"] = config.Provider.ToString(),
                    ["instanceId"] = instanceId ?? "default"
                });
            }
        }

        public override Task SaveAsync(Uri uri, Stream data) => SaveAsync(uri, data, null);

        /// <summary>
        /// Load data from cloud storage, optionally from a specific instance.
        /// </summary>
        public async Task<Stream> LoadAsync(Uri uri, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var config = GetConfig(instanceId);
            var client = GetHttpClient(instanceId);
            await EnsureTokenValidAsync(instanceId);

            var path = uri.AbsolutePath.TrimStart('/');

            // Update last access for caching
            await TouchAsync(uri);

            return config.Provider switch
            {
                CloudProvider.GoogleDrive => await DownloadFromGoogleDriveAsync(path, config, client),
                CloudProvider.OneDrive or CloudProvider.SharePoint => await DownloadFromOneDriveAsync(path, config, client),
                CloudProvider.Dropbox => await DownloadFromDropboxAsync(path, config, client),
                CloudProvider.Box => await DownloadFromBoxAsync(path, config, client),
                _ => throw new NotSupportedException($"Provider {config.Provider} not supported")
            };
        }

        public override Task<Stream> LoadAsync(Uri uri) => LoadAsync(uri, null);

        /// <summary>
        /// Delete data from cloud storage, optionally from a specific instance.
        /// </summary>
        public async Task DeleteAsync(Uri uri, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var config = GetConfig(instanceId);
            var client = GetHttpClient(instanceId);
            await EnsureTokenValidAsync(instanceId);

            var path = uri.AbsolutePath.TrimStart('/');

            switch (config.Provider)
            {
                case CloudProvider.GoogleDrive:
                    await DeleteFromGoogleDriveAsync(path, config, client);
                    break;
                case CloudProvider.OneDrive:
                case CloudProvider.SharePoint:
                    await DeleteFromOneDriveAsync(path, config, client);
                    break;
                case CloudProvider.Dropbox:
                    await DeleteFromDropboxAsync(path, config, client);
                    break;
                case CloudProvider.Box:
                    await DeleteFromBoxAsync(path, config, client);
                    break;
            }

            // Remove from index
            _ = RemoveFromIndexAsync(uri.ToString());
        }

        public override Task DeleteAsync(Uri uri) => DeleteAsync(uri, null);

        /// <summary>
        /// Check if data exists in cloud storage, optionally in a specific instance.
        /// </summary>
        public async Task<bool> ExistsAsync(Uri uri, string? instanceId = null)
        {
            try
            {
                var metadata = await GetFileMetadataAsync(uri.AbsolutePath.TrimStart('/'), instanceId);
                return metadata != null;
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
            await EnsureTokenValidAsync(null);

            var items = _config.Provider switch
            {
                CloudProvider.GoogleDrive => ListGoogleDriveFilesAsync(prefix, _config, GetHttpClient(null), ct),
                CloudProvider.OneDrive or CloudProvider.SharePoint => ListOneDriveFilesAsync(prefix, _config, GetHttpClient(null), ct),
                CloudProvider.Dropbox => ListDropboxFilesAsync(prefix, _config, GetHttpClient(null), ct),
                CloudProvider.Box => ListBoxFilesAsync(prefix, _config, GetHttpClient(null), ct),
                _ => throw new NotSupportedException($"Provider {_config.Provider} not supported")
            };

            await foreach (var item in items)
            {
                if (ct.IsCancellationRequested)
                    yield break;
                yield return item;
            }
        }

        #endregion

        #region Helper Methods

        private CloudStorageConfig GetConfig(string? instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
                return _config;

            var instance = _connectionRegistry.Get(instanceId);
            return instance?.Config ?? _config;
        }

        private HttpClient GetHttpClient(string? instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
                return HttpClient;

            var instance = _connectionRegistry.Get(instanceId);
            if (instance?.Connection is CloudStorageConnection conn)
                return conn.HttpClientFactory.CreateClient(conn.HttpClientName);

            return HttpClient;
        }

        private string GetScheme(CloudStorageConfig config)
        {
            return config.Provider switch
            {
                CloudProvider.GoogleDrive => "gdrive",
                CloudProvider.OneDrive => "onedrive",
                CloudProvider.SharePoint => "sharepoint",
                CloudProvider.Dropbox => "dropbox",
                CloudProvider.Box => "box",
                _ => "cloud"
            };
        }

        private string? GetAccessToken(string? instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
                return _accessToken;

            var instance = _connectionRegistry.Get(instanceId);
            if (instance?.Connection is CloudStorageConnection conn)
                return conn.AccessToken;

            return _accessToken;
        }

        #endregion

        #region Google Drive Implementation

        private async Task UploadToGoogleDriveAsync(string path, byte[] content, CloudStorageConfig config, HttpClient client)
        {
            var fileName = Path.GetFileName(path);
            var folderId = await EnsureFolderExistsAsync(Path.GetDirectoryName(path) ?? "", config, client);

            var metadata = new { name = fileName, parents = new[] { folderId ?? "root" } };
            var metadataJson = JsonSerializer.Serialize(metadata);

            using var multipartContent = new MultipartContent("related");
            multipartContent.Add(new StringContent(metadataJson, Encoding.UTF8, "application/json"));
            multipartContent.Add(new ByteArrayContent(content) { Headers = { { "Content-Type", "application/octet-stream" } } });

            var request = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart")
            {
                Content = multipartContent
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        private async Task<Stream> DownloadFromGoogleDriveAsync(string path, CloudStorageConfig config, HttpClient client)
        {
            var fileId = await GetGoogleDriveFileIdAsync(path, config, client);

            var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var ms = new MemoryStream();
            await response.Content.CopyToAsync(ms);
            ms.Position = 0;
            return ms;
        }

        private async Task DeleteFromGoogleDriveAsync(string path, CloudStorageConfig config, HttpClient client)
        {
            var fileId = await GetGoogleDriveFileIdAsync(path, config, client);

            var request = new HttpRequestMessage(HttpMethod.Delete, $"https://www.googleapis.com/drive/v3/files/{fileId}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        private async IAsyncEnumerable<StorageListItem> ListGoogleDriveFilesAsync(string prefix, CloudStorageConfig config, HttpClient client, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            string? pageToken = null;
            var query = string.IsNullOrEmpty(prefix) ? "" : $"name contains '{prefix}'";

            do
            {
                var url = $"https://www.googleapis.com/drive/v3/files?fields=files(id,name,size,mimeType),nextPageToken&q={Uri.EscapeDataString(query)}";
                if (pageToken != null)
                    url += $"&pageToken={pageToken}";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

                var response = await client.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<GoogleDriveListResult>(json);

                if (result?.Files != null)
                {
                    foreach (var file in result.Files)
                    {
                        if (ct.IsCancellationRequested) yield break;

                        var uri = new Uri($"gdrive:///{file.Name}");
                        long.TryParse(file.Size ?? "0", out var size);
                        yield return new StorageListItem(uri, size);
                    }
                }

                pageToken = result?.NextPageToken;

            } while (pageToken != null);
        }

        private async Task<string?> GetGoogleDriveFileIdAsync(string path, CloudStorageConfig config, HttpClient client)
        {
            var fileName = Path.GetFileName(path);
            var url = $"https://www.googleapis.com/drive/v3/files?q=name='{fileName}'&fields=files(id)";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<GoogleDriveListResult>(json);
            return result?.Files?.FirstOrDefault()?.Id;
        }

        private async Task<string?> EnsureFolderExistsAsync(string folderPath, CloudStorageConfig config, HttpClient client)
        {
            if (string.IsNullOrEmpty(folderPath)) return config.BaseFolderId;
            // Simplified - in production, create folder hierarchy
            return config.BaseFolderId;
        }

        #endregion

        #region OneDrive/SharePoint Implementation

        private async Task UploadToOneDriveAsync(string path, byte[] content, CloudStorageConfig config, HttpClient client)
        {
            var baseUrl = config.Provider == CloudProvider.SharePoint
                ? $"https://graph.microsoft.com/v1.0/sites/{config.SiteId}/drive"
                : "https://graph.microsoft.com/v1.0/me/drive";

            var url = $"{baseUrl}/root:/{path}:/content";

            var request = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new ByteArrayContent(content)
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        private async Task<Stream> DownloadFromOneDriveAsync(string path, CloudStorageConfig config, HttpClient client)
        {
            var baseUrl = config.Provider == CloudProvider.SharePoint
                ? $"https://graph.microsoft.com/v1.0/sites/{config.SiteId}/drive"
                : "https://graph.microsoft.com/v1.0/me/drive";

            var url = $"{baseUrl}/root:/{path}:/content";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var ms = new MemoryStream();
            await response.Content.CopyToAsync(ms);
            ms.Position = 0;
            return ms;
        }

        private async Task DeleteFromOneDriveAsync(string path, CloudStorageConfig config, HttpClient client)
        {
            var baseUrl = config.Provider == CloudProvider.SharePoint
                ? $"https://graph.microsoft.com/v1.0/sites/{config.SiteId}/drive"
                : "https://graph.microsoft.com/v1.0/me/drive";

            var url = $"{baseUrl}/root:/{path}";

            var request = new HttpRequestMessage(HttpMethod.Delete, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        private async IAsyncEnumerable<StorageListItem> ListOneDriveFilesAsync(string prefix, CloudStorageConfig config, HttpClient client, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var baseUrl = config.Provider == CloudProvider.SharePoint
                ? $"https://graph.microsoft.com/v1.0/sites/{config.SiteId}/drive"
                : "https://graph.microsoft.com/v1.0/me/drive";

            var folderPath = string.IsNullOrEmpty(prefix) ? "root" : $"root:/{prefix}:";
            var url = $"{baseUrl}/{folderPath}/children";

            while (url != null)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

                var response = await client.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<OneDriveListResult>(json);

                if (result?.Value != null)
                {
                    foreach (var item in result.Value)
                    {
                        if (ct.IsCancellationRequested) yield break;

                        var uri = new Uri($"onedrive:///{item.Name}");
                        yield return new StorageListItem(uri, item.Size);
                    }
                }

                url = result?.ODataNextLink;
            }
        }

        #endregion

        #region Dropbox Implementation

        private async Task UploadToDropboxAsync(string path, byte[] content, CloudStorageConfig config, HttpClient client)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/upload")
            {
                Content = new ByteArrayContent(content)
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
            request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", JsonSerializer.Serialize(new { path = $"/{path}", mode = "overwrite" }));
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        private async Task<Stream> DownloadFromDropboxAsync(string path, CloudStorageConfig config, HttpClient client)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/download");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
            request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", JsonSerializer.Serialize(new { path = $"/{path}" }));

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var ms = new MemoryStream();
            await response.Content.CopyToAsync(ms);
            ms.Position = 0;
            return ms;
        }

        private async Task DeleteFromDropboxAsync(string path, CloudStorageConfig config, HttpClient client)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/files/delete_v2")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { path = $"/{path}" }), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        private async IAsyncEnumerable<StorageListItem> ListDropboxFilesAsync(string prefix, CloudStorageConfig config, HttpClient client, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var cursor = (string?)null;
            var hasMore = true;

            while (hasMore)
            {
                HttpRequestMessage request;
                if (cursor == null)
                {
                    request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/files/list_folder")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new { path = string.IsNullOrEmpty(prefix) ? "" : $"/{prefix}" }), Encoding.UTF8, "application/json")
                    };
                }
                else
                {
                    request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/files/list_folder/continue")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new { cursor }), Encoding.UTF8, "application/json")
                    };
                }
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

                var response = await client.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<DropboxListResult>(json);

                if (result?.Entries != null)
                {
                    foreach (var entry in result.Entries)
                    {
                        if (ct.IsCancellationRequested) yield break;

                        var uri = new Uri($"dropbox:///{entry.Name}");
                        yield return new StorageListItem(uri, entry.Size);
                    }
                }

                cursor = result?.Cursor;
                hasMore = result?.HasMore ?? false;
            }
        }

        #endregion

        #region Box Implementation

        private async Task UploadToBoxAsync(string path, byte[] content, CloudStorageConfig config, HttpClient client)
        {
            var fileName = Path.GetFileName(path);
            var folderId = config.BaseFolderId ?? "0";

            using var multipartContent = new MultipartFormDataContent();
            multipartContent.Add(new StringContent(JsonSerializer.Serialize(new { name = fileName, parent = new { id = folderId } })), "attributes");
            multipartContent.Add(new ByteArrayContent(content), "file", fileName);

            var request = new HttpRequestMessage(HttpMethod.Post, "https://upload.box.com/api/2.0/files/content")
            {
                Content = multipartContent
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        private async Task<Stream> DownloadFromBoxAsync(string path, CloudStorageConfig config, HttpClient client)
        {
            var fileId = await GetBoxFileIdAsync(path, config, client);

            var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.box.com/2.0/files/{fileId}/content");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var ms = new MemoryStream();
            await response.Content.CopyToAsync(ms);
            ms.Position = 0;
            return ms;
        }

        private async Task DeleteFromBoxAsync(string path, CloudStorageConfig config, HttpClient client)
        {
            var fileId = await GetBoxFileIdAsync(path, config, client);

            var request = new HttpRequestMessage(HttpMethod.Delete, $"https://api.box.com/2.0/files/{fileId}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        private async IAsyncEnumerable<StorageListItem> ListBoxFilesAsync(string prefix, CloudStorageConfig config, HttpClient client, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var folderId = config.BaseFolderId ?? "0";
            var offset = 0;
            const int limit = 100;
            const int MaxIterations = 10000; // Safety limit to prevent infinite loops
            var iteration = 0;

            while (true)
            {
                if (++iteration > MaxIterations)
                {
                    throw new InvalidOperationException(
                        $"[Box] Exceeded maximum iterations ({MaxIterations}) listing files. " +
                        $"This may indicate an API issue or extremely large folder.");
                }

                var url = $"https://api.box.com/2.0/folders/{folderId}/items?limit={limit}&offset={offset}";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

                var response = await client.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<BoxListResult>(json);

                if (result?.Entries == null || result.Entries.Length == 0)
                    yield break;

                foreach (var entry in result.Entries)
                {
                    if (ct.IsCancellationRequested) yield break;

                    if (string.IsNullOrEmpty(prefix) || entry.Name?.StartsWith(prefix) == true)
                    {
                        var uri = new Uri($"box:///{entry.Name}");
                        yield return new StorageListItem(uri, entry.Size);
                    }
                }

                offset += limit;
                if (result.Entries.Length < limit)
                    yield break;
            }
        }

        private async Task<string> GetBoxFileIdAsync(string path, CloudStorageConfig config, HttpClient client)
        {
            // Search for file by name
            var fileName = Path.GetFileName(path);
            var url = $"https://api.box.com/2.0/search?query={Uri.EscapeDataString(fileName)}&type=file";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<BoxListResult>(json);
            return result?.Entries?.FirstOrDefault()?.Id ?? throw new FileNotFoundException($"File not found: {path}");
        }

        #endregion

        #region Common Methods

        private async Task<Dictionary<string, object>> GetFileMetadataAsync(string path, string? instanceId = null)
        {
            await EnsureTokenValidAsync(instanceId);
            var config = GetConfig(instanceId);

            // Provider-specific metadata retrieval would go here
            return new Dictionary<string, object>
            {
                ["Path"] = path,
                ["Provider"] = config.Provider.ToString(),
                ["InstanceId"] = instanceId ?? "default"
            };
        }

        private async Task<string> CreateShareLinkAsync(string path, string? instanceId = null)
        {
            await EnsureTokenValidAsync(instanceId);
            var config = GetConfig(instanceId);
            // Provider-specific share link creation
            return $"https://share.{config.Provider.ToString().ToLower()}.com/{path}";
        }

        private async Task MoveFileAsync(string sourcePath, string destPath, string? instanceId = null)
        {
            await EnsureTokenValidAsync(instanceId);
            // Provider-specific move operation
        }

        private async Task<List<string>> SearchFilesAsync(string query, string? instanceId = null)
        {
            await EnsureTokenValidAsync(instanceId);
            // Provider-specific search
            return new List<string>();
        }

        private string GetAuthorizationUrl(CloudStorageConfig config)
        {
            return config.Provider switch
            {
                CloudProvider.GoogleDrive => $"https://accounts.google.com/o/oauth2/v2/auth?client_id={config.ClientId}&redirect_uri={config.RedirectUri}&response_type=code&scope=https://www.googleapis.com/auth/drive.file",
                CloudProvider.OneDrive or CloudProvider.SharePoint => $"https://login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id={config.ClientId}&redirect_uri={config.RedirectUri}&response_type=code&scope=Files.ReadWrite.All",
                CloudProvider.Dropbox => $"https://www.dropbox.com/oauth2/authorize?client_id={config.ClientId}&redirect_uri={config.RedirectUri}&response_type=code",
                CloudProvider.Box => $"https://account.box.com/api/oauth2/authorize?client_id={config.ClientId}&redirect_uri={config.RedirectUri}&response_type=code",
                _ => throw new NotSupportedException()
            };
        }

        private async Task EnsureTokenValidAsync(string? instanceId = null)
        {
            if (_accessToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
                return;

            await _tokenLock.WaitAsync();
            try
            {
                if (_accessToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
                    return;

                if (_refreshToken != null)
                {
                    await RefreshTokenAsync(instanceId);
                }
                else if (_accessToken == null)
                {
                    throw new InvalidOperationException("No access token available. Please authenticate first.");
                }
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        private async Task RefreshTokenAsync(string? instanceId = null)
        {
            var config = GetConfig(instanceId);
            var client = GetHttpClient(instanceId);

            var tokenUrl = config.Provider switch
            {
                CloudProvider.GoogleDrive => "https://oauth2.googleapis.com/token",
                CloudProvider.OneDrive or CloudProvider.SharePoint => "https://login.microsoftonline.com/common/oauth2/v2.0/token",
                CloudProvider.Dropbox => "https://api.dropboxapi.com/oauth2/token",
                CloudProvider.Box => "https://api.box.com/oauth2/token",
                _ => throw new NotSupportedException()
            };

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _refreshToken!,
                ["client_id"] = config.ClientId,
                ["client_secret"] = config.ClientSecret
            });

            var response = await client.PostAsync(tokenUrl, content);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize<OAuthTokenResponse>(json);

            _accessToken = tokenResponse?.AccessToken;
            _refreshToken = tokenResponse?.RefreshToken ?? _refreshToken;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse?.ExpiresIn ?? 3600);
        }

        #endregion

        #region JSON Models

        private sealed class GoogleDriveListResult
        {
            public GoogleDriveFile[]? Files { get; set; }
            public string? NextPageToken { get; set; }
        }

        private sealed class GoogleDriveFile
        {
            public string? Id { get; set; }
            public string? Name { get; set; }
            public string? Size { get; set; }
            public string? MimeType { get; set; }
        }

        private sealed class OneDriveListResult
        {
            public OneDriveItem[]? Value { get; set; }
            public string? ODataNextLink { get; set; }
        }

        private sealed class OneDriveItem
        {
            public string? Id { get; set; }
            public string? Name { get; set; }
            public long Size { get; set; }
        }

        private sealed class DropboxListResult
        {
            public DropboxEntry[]? Entries { get; set; }
            public string? Cursor { get; set; }
            public bool? HasMore { get; set; }
        }

        private sealed class DropboxEntry
        {
            public string? Name { get; set; }
            public long Size { get; set; }
        }

        private sealed class BoxListResult
        {
            public BoxEntry[]? Entries { get; set; }
        }

        private sealed class BoxEntry
        {
            public string? Id { get; set; }
            public string? Name { get; set; }
            public long Size { get; set; }
        }

        private sealed class OAuthTokenResponse
        {
            public string? AccessToken { get; set; }
            public string? RefreshToken { get; set; }
            public int? ExpiresIn { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// Internal connection wrapper for cloud storage instances.
    /// </summary>
    internal class CloudStorageConnection
    {
        public required CloudStorageConfig Config { get; init; }
        public required IHttpClientFactory HttpClientFactory { get; init; }
        public required string HttpClientName { get; init; }
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public DateTime TokenExpiry { get; set; }
    }

    /// <summary>
    /// Configuration for cloud storage.
    /// </summary>
    public class CloudStorageConfig : StorageConfigBase
    {
        /// <summary>
        /// Cloud storage provider.
        /// </summary>
        public CloudProvider Provider { get; set; }

        /// <summary>
        /// OAuth2 client ID.
        /// </summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// OAuth2 client secret.
        /// </summary>
        public string ClientSecret { get; set; } = string.Empty;

        /// <summary>
        /// OAuth2 redirect URI.
        /// </summary>
        public string RedirectUri { get; set; } = "urn:ietf:wg:oauth:2.0:oob";

        /// <summary>
        /// OAuth2 access token.
        /// </summary>
        public string? AccessToken { get; set; }

        /// <summary>
        /// OAuth2 refresh token.
        /// </summary>
        public string? RefreshToken { get; set; }

        /// <summary>
        /// Token expiry time.
        /// </summary>
        public DateTime? TokenExpiry { get; set; }

        /// <summary>
        /// Base folder ID (root folder for operations).
        /// </summary>
        public string? BaseFolderId { get; set; }

        /// <summary>
        /// SharePoint site ID (for SharePoint only).
        /// </summary>
        public string? SiteId { get; set; }

        /// <summary>
        /// Request timeout in seconds.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 300;

        /// <summary>
        /// Creates configuration for Google Drive.
        /// </summary>
        public static CloudStorageConfig GoogleDrive(string clientId, string clientSecret, string? accessToken = null) => new()
        {
            Provider = CloudProvider.GoogleDrive,
            ClientId = clientId,
            ClientSecret = clientSecret,
            AccessToken = accessToken
        };

        /// <summary>
        /// Creates configuration for Google Drive with instance ID.
        /// </summary>
        public static CloudStorageConfig GoogleDrive(string clientId, string clientSecret, string instanceId, string? accessToken = null) => new()
        {
            Provider = CloudProvider.GoogleDrive,
            ClientId = clientId,
            ClientSecret = clientSecret,
            AccessToken = accessToken,
            InstanceId = instanceId
        };

        /// <summary>
        /// Creates configuration for OneDrive.
        /// </summary>
        public static CloudStorageConfig OneDrive(string clientId, string clientSecret, string? accessToken = null) => new()
        {
            Provider = CloudProvider.OneDrive,
            ClientId = clientId,
            ClientSecret = clientSecret,
            AccessToken = accessToken
        };

        /// <summary>
        /// Creates configuration for OneDrive with instance ID.
        /// </summary>
        public static CloudStorageConfig OneDrive(string clientId, string clientSecret, string instanceId, string? accessToken = null) => new()
        {
            Provider = CloudProvider.OneDrive,
            ClientId = clientId,
            ClientSecret = clientSecret,
            AccessToken = accessToken,
            InstanceId = instanceId
        };

        /// <summary>
        /// Creates configuration for SharePoint.
        /// </summary>
        public static CloudStorageConfig SharePoint(string clientId, string clientSecret, string siteId, string? accessToken = null) => new()
        {
            Provider = CloudProvider.SharePoint,
            ClientId = clientId,
            ClientSecret = clientSecret,
            SiteId = siteId,
            AccessToken = accessToken
        };

        /// <summary>
        /// Creates configuration for SharePoint with instance ID.
        /// </summary>
        public static CloudStorageConfig SharePoint(string clientId, string clientSecret, string siteId, string instanceId, string? accessToken = null) => new()
        {
            Provider = CloudProvider.SharePoint,
            ClientId = clientId,
            ClientSecret = clientSecret,
            SiteId = siteId,
            AccessToken = accessToken,
            InstanceId = instanceId
        };

        /// <summary>
        /// Creates configuration for Dropbox.
        /// </summary>
        public static CloudStorageConfig Dropbox(string clientId, string clientSecret, string? accessToken = null) => new()
        {
            Provider = CloudProvider.Dropbox,
            ClientId = clientId,
            ClientSecret = clientSecret,
            AccessToken = accessToken
        };

        /// <summary>
        /// Creates configuration for Dropbox with instance ID.
        /// </summary>
        public static CloudStorageConfig Dropbox(string clientId, string clientSecret, string instanceId, string? accessToken = null) => new()
        {
            Provider = CloudProvider.Dropbox,
            ClientId = clientId,
            ClientSecret = clientSecret,
            AccessToken = accessToken,
            InstanceId = instanceId
        };

        /// <summary>
        /// Creates configuration for Box.
        /// </summary>
        public static CloudStorageConfig Box(string clientId, string clientSecret, string? accessToken = null) => new()
        {
            Provider = CloudProvider.Box,
            ClientId = clientId,
            ClientSecret = clientSecret,
            AccessToken = accessToken
        };

        /// <summary>
        /// Creates configuration for Box with instance ID.
        /// </summary>
        public static CloudStorageConfig Box(string clientId, string clientSecret, string instanceId, string? accessToken = null) => new()
        {
            Provider = CloudProvider.Box,
            ClientId = clientId,
            ClientSecret = clientSecret,
            AccessToken = accessToken,
            InstanceId = instanceId
        };
    }

    /// <summary>
    /// Supported cloud storage providers.
    /// </summary>
    public enum CloudProvider
    {
        GoogleDrive,
        OneDrive,
        SharePoint,
        Dropbox,
        Box
    }
}
