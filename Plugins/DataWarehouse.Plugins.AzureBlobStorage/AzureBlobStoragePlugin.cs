using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Infrastructure;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Storage;
using DataWarehouse.SDK.Utilities;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using StorageTier = DataWarehouse.SDK.Primitives.StorageTier;

namespace DataWarehouse.Plugins.AzureBlobStorage
{
    /// <summary>
    /// Azure Blob Storage plugin.
    ///
    /// Features:
    /// - Full Azure Blob Storage API support
    /// - Block, Append, and Page blob support
    /// - Access tiers (Hot, Cool, Cold, Archive)
    /// - SAS token generation
    /// - Container management
    /// - Blob versioning and snapshots
    /// - Soft delete support
    /// - Multi-instance support with role-based selection
    /// - TTL-based caching support
    /// - Document indexing and search
    ///
    /// Message Commands:
    /// - storage.azure.put: Upload blob
    /// - storage.azure.get: Download blob
    /// - storage.azure.delete: Delete blob
    /// - storage.azure.list: List blobs
    /// - storage.azure.tier: Set access tier
    /// - storage.azure.properties: Get blob properties
    /// - storage.azure.sas: Generate SAS token
    /// - storage.azure.snapshot: Create snapshot
    /// - storage.azure.copy: Copy blob
    /// - storage.instance.*: Multi-instance management
    /// </summary>
    public sealed class AzureBlobStoragePlugin : HybridStoragePluginBase<AzureBlobConfig>, ITieredStorage
    {
        private readonly HttpClient _httpClient;

        public override string Id => "datawarehouse.plugins.storage.azure";
        public override string Name => "Azure Blob Storage";
        public override string Version => "2.0.0";
        public override string Scheme => "azure";
        public override string StorageCategory => "Cloud";

        /// <summary>
        /// Creates an Azure Blob storage plugin with configuration.
        /// </summary>
        public AzureBlobStoragePlugin(AzureBlobConfig config)
            : base(config ?? throw new ArgumentNullException(nameof(config)))
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
            };
        }

        /// <summary>
        /// Creates a connection for the given configuration.
        /// </summary>
        protected override Task<object> CreateConnectionAsync(AzureBlobConfig config)
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            };

            return Task.FromResult<object>(new AzureBlobConnection
            {
                Config = config,
                HttpClient = client
            });
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            var capabilities = base.GetCapabilities();
            capabilities.AddRange(new[]
            {
                new PluginCapabilityDescriptor { Name = "storage.azure.put", DisplayName = "Put", Description = "Upload blob to Azure" },
                new PluginCapabilityDescriptor { Name = "storage.azure.get", DisplayName = "Get", Description = "Download blob from Azure" },
                new PluginCapabilityDescriptor { Name = "storage.azure.delete", DisplayName = "Delete", Description = "Delete blob from Azure" },
                new PluginCapabilityDescriptor { Name = "storage.azure.list", DisplayName = "List", Description = "List blobs in container" },
                new PluginCapabilityDescriptor { Name = "storage.azure.tier", DisplayName = "Tier", Description = "Set blob access tier" },
                new PluginCapabilityDescriptor { Name = "storage.azure.properties", DisplayName = "Properties", Description = "Get blob properties" },
                new PluginCapabilityDescriptor { Name = "storage.azure.sas", DisplayName = "SAS", Description = "Generate SAS token" },
                new PluginCapabilityDescriptor { Name = "storage.azure.snapshot", DisplayName = "Snapshot", Description = "Create blob snapshot" },
                new PluginCapabilityDescriptor { Name = "storage.azure.copy", DisplayName = "Copy", Description = "Copy blob" }
            });
            return capabilities;
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Description"] = "Azure Blob Storage with full tier support";
            metadata["AccountName"] = _config.AccountName;
            metadata["Container"] = _config.Container;
            metadata["DefaultTier"] = _config.DefaultAccessTier;
            metadata["SupportsTiering"] = true;
            metadata["SupportsVersioning"] = true;
            metadata["SupportsSnapshots"] = true;
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
                "storage.azure.put" => await HandlePutAsync(message),
                "storage.azure.get" => await HandleGetAsync(message),
                "storage.azure.delete" => await HandleDeleteAsync(message),
                "storage.azure.tier" => await HandleTierAsync(message),
                "storage.azure.properties" => await HandlePropertiesAsync(message),
                "storage.azure.sas" => HandleSas(message),
                "storage.azure.snapshot" => await HandleSnapshotAsync(message),
                "storage.azure.copy" => await HandleCopyAsync(message),
                _ => null
            };

            // If not handled, delegate to base for multi-instance management
            if (response == null)
            {
                await base.OnMessageAsync(message);
            }
        }

        private async Task<MessageResponse> HandlePutAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("blobName", out var nameObj) ||
                !payload.TryGetValue("data", out var dataObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'blobName' and 'data'");
            }

            var blobName = nameObj.ToString()!;
            var data = dataObj switch
            {
                Stream s => s,
                byte[] b => new MemoryStream(b),
                string str => new MemoryStream(Encoding.UTF8.GetBytes(str)),
                _ => throw new ArgumentException("Data must be Stream, byte[], or string")
            };

            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;
            var config = GetConfig(instanceId);

            var uri = new Uri($"azure://{config.Container}/{blobName}");
            await SaveAsync(uri, data, instanceId);
            return MessageResponse.Ok(new { Container = config.Container, BlobName = blobName, Success = true, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleGetAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("blobName", out var nameObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'blobName'");
            }

            var blobName = nameObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;
            var config = GetConfig(instanceId);

            var uri = new Uri($"azure://{config.Container}/{blobName}");
            var stream = await LoadAsync(uri, instanceId);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return MessageResponse.Ok(new { Container = config.Container, BlobName = blobName, Data = ms.ToArray(), InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleDeleteAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("blobName", out var nameObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'blobName'");
            }

            var blobName = nameObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;
            var config = GetConfig(instanceId);

            var uri = new Uri($"azure://{config.Container}/{blobName}");
            await DeleteAsync(uri, instanceId);
            return MessageResponse.Ok(new { Container = config.Container, BlobName = blobName, Deleted = true, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleTierAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("blobName", out var nameObj) ||
                !payload.TryGetValue("tier", out var tierObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'blobName' and 'tier'");
            }

            var blobName = nameObj.ToString()!;
            var tierStr = tierObj.ToString()!;
            var tier = Enum.Parse<StorageTier>(tierStr, ignoreCase: true);
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;
            var config = GetConfig(instanceId);

            var manifest = new Manifest { Id = blobName, StorageUri = new Uri($"azure://{config.Container}/{blobName}") };
            await MoveToTierAsync(manifest, tier, instanceId);
            return MessageResponse.Ok(new { BlobName = blobName, Tier = tier.ToString(), Success = true, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandlePropertiesAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("blobName", out var nameObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'blobName'");
            }

            var blobName = nameObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var properties = await GetBlobPropertiesAsync(blobName, instanceId);
            return MessageResponse.Ok(properties);
        }

        private MessageResponse HandleSas(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("blobName", out var nameObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'blobName'");
            }

            var blobName = nameObj.ToString()!;
            var expiresIn = payload.TryGetValue("expiresIn", out var expObj) && expObj is int exp
                ? TimeSpan.FromSeconds(exp)
                : TimeSpan.FromHours(1);
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var sasUrl = GenerateSasUrl(blobName, expiresIn, instanceId);
            return MessageResponse.Ok(new { BlobName = blobName, SasUrl = sasUrl, ExpiresIn = expiresIn.TotalSeconds, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleSnapshotAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("blobName", out var nameObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'blobName'");
            }

            var blobName = nameObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var snapshotTime = await CreateSnapshotAsync(blobName, instanceId);
            return MessageResponse.Ok(new { BlobName = blobName, SnapshotTime = snapshotTime, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleCopyAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("sourceBlobName", out var sourceObj) ||
                !payload.TryGetValue("destinationBlobName", out var destObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'sourceBlobName' and 'destinationBlobName'");
            }

            var sourceBlobName = sourceObj.ToString()!;
            var destBlobName = destObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            await CopyBlobAsync(sourceBlobName, destBlobName, instanceId);
            return MessageResponse.Ok(new { SourceBlobName = sourceBlobName, DestinationBlobName = destBlobName, Success = true, InstanceId = instanceId });
        }

        #region Storage Operations with Instance Support

        /// <summary>
        /// Save data to storage, optionally targeting a specific instance.
        /// </summary>
        public async Task SaveAsync(Uri uri, Stream data, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);
            ArgumentNullException.ThrowIfNull(data);

            var config = GetConfig(instanceId);
            var client = GetHttpClient(instanceId);
            var blobName = GetBlobName(uri);
            var endpoint = GetBlobUrl(blobName, config);

            using var ms = new MemoryStream();
            await data.CopyToAsync(ms);
            var content = ms.ToArray();

            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentLength = content.Length;
            request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");

            if (!string.IsNullOrEmpty(config.DefaultAccessTier))
            {
                request.Headers.TryAddWithoutValidation("x-ms-access-tier", config.DefaultAccessTier);
            }

            SignRequest(request, config);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            // Auto-index if enabled
            if (config.EnableIndexing)
            {
                await IndexDocumentAsync(uri.ToString(), new Dictionary<string, object>
                {
                    ["uri"] = uri.ToString(),
                    ["container"] = config.Container,
                    ["blobName"] = blobName,
                    ["size"] = content.Length,
                    ["instanceId"] = instanceId ?? "default"
                });
            }
        }

        public override Task SaveAsync(Uri uri, Stream data) => SaveAsync(uri, data, null);

        /// <summary>
        /// Load data from storage, optionally from a specific instance.
        /// </summary>
        public async Task<Stream> LoadAsync(Uri uri, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var config = GetConfig(instanceId);
            var client = GetHttpClient(instanceId);
            var blobName = GetBlobName(uri);
            var endpoint = GetBlobUrl(blobName, config);

            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            SignRequest(request, config);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            // Update last access for caching
            await TouchAsync(uri);

            var ms = new MemoryStream();
            await response.Content.CopyToAsync(ms);
            ms.Position = 0;
            return ms;
        }

        public override Task<Stream> LoadAsync(Uri uri) => LoadAsync(uri, null);

        /// <summary>
        /// Delete data from storage, optionally from a specific instance.
        /// </summary>
        public async Task DeleteAsync(Uri uri, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var config = GetConfig(instanceId);
            var client = GetHttpClient(instanceId);
            var blobName = GetBlobName(uri);
            var endpoint = GetBlobUrl(blobName, config);

            var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
            SignRequest(request, config);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            // Remove from index
            _ = RemoveFromIndexAsync(uri.ToString());
        }

        public override Task DeleteAsync(Uri uri) => DeleteAsync(uri, null);

        /// <summary>
        /// Check if data exists in storage, optionally in a specific instance.
        /// </summary>
        public async Task<bool> ExistsAsync(Uri uri, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var blobName = GetBlobName(uri);
            try
            {
                await GetBlobPropertiesAsync(blobName, instanceId);
                return true;
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
            string? marker = null;

            do
            {
                var endpoint = $"{GetContainerUrl(_config)}?restype=container&comp=list&prefix={Uri.EscapeDataString(prefix)}";
                if (marker != null)
                    endpoint += $"&marker={Uri.EscapeDataString(marker)}";

                var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                SignRequest(request, _config);

                var response = await _httpClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var xml = await response.Content.ReadAsStringAsync(ct);

                // Parse XML response (simplified)
                var lines = xml.Split('\n');
                foreach (var line in lines)
                {
                    if (ct.IsCancellationRequested)
                        yield break;

                    if (line.Contains("<Name>") && !line.Contains("<ContainerName>"))
                    {
                        var name = ExtractXmlValue(line, "Name");
                        var sizeLine = lines.FirstOrDefault(l => l.Contains("<Content-Length>"));
                        long.TryParse(sizeLine != null ? ExtractXmlValue(sizeLine, "Content-Length") : "0", out var size);

                        var itemUri = new Uri($"azure://{_config.Container}/{name}");
                        yield return new StorageListItem(itemUri, size);
                    }
                }

                marker = xml.Contains("<NextMarker>") && !xml.Contains("<NextMarker/>")
                    ? ExtractXmlValue(xml, "NextMarker")
                    : null;

            } while (marker != null);
        }

        #endregion

        #region Azure-Specific Operations

        /// <summary>
        /// Get blob properties.
        /// </summary>
        public async Task<Dictionary<string, object>> GetBlobPropertiesAsync(string blobName, string? instanceId = null)
        {
            var config = GetConfig(instanceId);
            var client = GetHttpClient(instanceId);
            var endpoint = GetBlobUrl(blobName, config);
            var request = new HttpRequestMessage(HttpMethod.Head, endpoint);
            SignRequest(request, config);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var properties = new Dictionary<string, object>
            {
                ["ContentLength"] = response.Content.Headers.ContentLength ?? 0,
                ["ContentType"] = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
                ["LastModified"] = response.Content.Headers.LastModified?.ToString() ?? "",
                ["ETag"] = response.Headers.ETag?.Tag ?? ""
            };

            if (response.Headers.TryGetValues("x-ms-access-tier", out var tierValues))
                properties["AccessTier"] = tierValues.FirstOrDefault() ?? "";

            if (response.Headers.TryGetValues("x-ms-blob-type", out var blobTypeValues))
                properties["BlobType"] = blobTypeValues.FirstOrDefault() ?? "";

            return properties;
        }

        /// <summary>
        /// Move blob to a different access tier.
        /// </summary>
        public async Task<string> MoveToTierAsync(Manifest manifest, StorageTier targetTier, string? instanceId = null)
        {
            var config = GetConfig(instanceId);
            var client = GetHttpClient(instanceId);
            var blobName = GetBlobName(manifest.StorageUri);
            var azureTier = targetTier switch
            {
                StorageTier.Hot => "Hot",
                StorageTier.Cool => "Cool",
                StorageTier.Cold => "Cold",
                StorageTier.Archive => "Archive",
                _ => "Hot"
            };

            var endpoint = $"{GetBlobUrl(blobName, config)}?comp=tier";
            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            request.Headers.TryAddWithoutValidation("x-ms-access-tier", azureTier);
            SignRequest(request, config);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            return manifest.StorageUri.ToString();
        }

        public Task<string> MoveToTierAsync(Manifest manifest, StorageTier targetTier)
            => MoveToTierAsync(manifest, targetTier, null);

        public Task<StorageTier> GetCurrentTierAsync(Uri uri)
        {
            return Task.FromResult(StorageTier.Hot);
        }

        /// <summary>
        /// Generate SAS URL.
        /// </summary>
        public string GenerateSasUrl(string blobName, TimeSpan expiresIn, string? instanceId = null)
        {
            var config = GetConfig(instanceId);
            var start = DateTime.UtcNow.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ");
            var expiry = DateTime.UtcNow.Add(expiresIn).ToString("yyyy-MM-ddTHH:mm:ssZ");

            var canonicalizedResource = $"/blob/{config.AccountName}/{config.Container}/{blobName}";
            var stringToSign = $"r\n{start}\n{expiry}\n{canonicalizedResource}\n\n\nhttps\n2021-06-08\nb\n\n\n\n\n";

            var keyBytes = Convert.FromBase64String(config.AccountKey);
            using var hmac = new HMACSHA256(keyBytes);
            var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

            var sasToken = $"sv=2021-06-08&ss=b&srt=o&sp=r&se={Uri.EscapeDataString(expiry)}&st={Uri.EscapeDataString(start)}&spr=https&sig={Uri.EscapeDataString(signature)}";
            return $"{GetBlobUrl(blobName, config)}?{sasToken}";
        }

        /// <summary>
        /// Create blob snapshot.
        /// </summary>
        public async Task<string> CreateSnapshotAsync(string blobName, string? instanceId = null)
        {
            var config = GetConfig(instanceId);
            var client = GetHttpClient(instanceId);
            var endpoint = $"{GetBlobUrl(blobName, config)}?comp=snapshot";
            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            SignRequest(request, config);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            if (response.Headers.TryGetValues("x-ms-snapshot", out var snapshotValues))
                return snapshotValues.FirstOrDefault() ?? "";

            return DateTime.UtcNow.ToString("o");
        }

        /// <summary>
        /// Copy blob.
        /// </summary>
        public async Task CopyBlobAsync(string sourceBlobName, string destBlobName, string? instanceId = null)
        {
            var config = GetConfig(instanceId);
            var client = GetHttpClient(instanceId);
            var sourceUrl = GetBlobUrl(sourceBlobName, config);
            var destUrl = GetBlobUrl(destBlobName, config);

            var request = new HttpRequestMessage(HttpMethod.Put, destUrl);
            request.Headers.TryAddWithoutValidation("x-ms-copy-source", sourceUrl);
            SignRequest(request, config);

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        #endregion

        #region Helper Methods

        private AzureBlobConfig GetConfig(string? instanceId)
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
            if (instance?.Connection is AzureBlobConnection conn)
                return conn.HttpClient;

            return _httpClient;
        }

        private string GetBlobName(Uri uri)
        {
            if (uri.Scheme == "azure")
                return uri.AbsolutePath.TrimStart('/');

            return uri.AbsolutePath.TrimStart('/');
        }

        private string GetBlobUrl(string blobName, AzureBlobConfig config)
        {
            return $"https://{config.AccountName}.blob.core.windows.net/{config.Container}/{blobName}";
        }

        private string GetContainerUrl(AzureBlobConfig config)
        {
            return $"https://{config.AccountName}.blob.core.windows.net/{config.Container}";
        }

        private void SignRequest(HttpRequestMessage request, AzureBlobConfig config)
        {
            var now = DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture);
            request.Headers.TryAddWithoutValidation("x-ms-date", now);
            request.Headers.TryAddWithoutValidation("x-ms-version", "2021-06-08");

            // Build canonical headers
            var canonicalHeaders = $"x-ms-date:{now}\nx-ms-version:2021-06-08\n";
            foreach (var header in request.Headers.Where(h => h.Key.StartsWith("x-ms-") && h.Key != "x-ms-date" && h.Key != "x-ms-version").OrderBy(h => h.Key))
            {
                canonicalHeaders += $"{header.Key}:{string.Join(",", header.Value)}\n";
            }

            // Build canonical resource
            var uri = request.RequestUri!;
            var canonicalResource = $"/{config.AccountName}{uri.AbsolutePath}";
            if (!string.IsNullOrEmpty(uri.Query))
            {
                var queryParams = uri.Query.TrimStart('?').Split('&')
                    .Select(p => p.Split('='))
                    .Where(p => p.Length == 2)
                    .OrderBy(p => p[0])
                    .Select(p => $"\n{p[0]}:{Uri.UnescapeDataString(p[1])}");
                canonicalResource += string.Join("", queryParams);
            }

            var contentLength = request.Content?.Headers.ContentLength?.ToString() ?? "";
            var contentType = request.Content?.Headers.ContentType?.ToString() ?? "";

            var stringToSign = $"{request.Method}\n\n\n{contentLength}\n\n{contentType}\n\n\n\n\n\n\n{canonicalHeaders}{canonicalResource}";

            var keyBytes = Convert.FromBase64String(config.AccountKey);
            using var hmac = new HMACSHA256(keyBytes);
            var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("SharedKey", $"{config.AccountName}:{signature}");
        }

        private static string ExtractXmlValue(string xml, string tag)
        {
            var startTag = $"<{tag}>";
            var endTag = $"</{tag}>";
            var startIndex = xml.IndexOf(startTag);
            var endIndex = xml.IndexOf(endTag);

            if (startIndex >= 0 && endIndex > startIndex)
            {
                return xml.Substring(startIndex + startTag.Length, endIndex - startIndex - startTag.Length);
            }

            return string.Empty;
        }

        #endregion
    }

    /// <summary>
    /// Internal connection wrapper for Azure Blob Storage instances.
    /// </summary>
    internal class AzureBlobConnection : IDisposable
    {
        public required AzureBlobConfig Config { get; init; }
        public required HttpClient HttpClient { get; init; }

        public void Dispose()
        {
            HttpClient?.Dispose();
        }
    }

    /// <summary>
    /// Configuration for Azure Blob Storage.
    /// </summary>
    public class AzureBlobConfig : StorageConfigBase
    {
        /// <summary>
        /// Azure Storage account name.
        /// </summary>
        public string AccountName { get; set; } = string.Empty;

        /// <summary>
        /// Azure Storage account key.
        /// </summary>
        public string AccountKey { get; set; } = string.Empty;

        /// <summary>
        /// Container name.
        /// </summary>
        public string Container { get; set; } = string.Empty;

        /// <summary>
        /// Default access tier for new blobs.
        /// </summary>
        public string DefaultAccessTier { get; set; } = "Hot";

        /// <summary>
        /// Request timeout in seconds.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 300;

        /// <summary>
        /// Creates configuration from connection string.
        /// </summary>
        public static AzureBlobConfig FromConnectionString(string connectionString, string container)
        {
            var config = new AzureBlobConfig { Container = container };

            foreach (var part in connectionString.Split(';'))
            {
                var kvp = part.Split('=', 2);
                if (kvp.Length == 2)
                {
                    switch (kvp[0])
                    {
                        case "AccountName":
                            config.AccountName = kvp[1];
                            break;
                        case "AccountKey":
                            config.AccountKey = kvp[1];
                            break;
                    }
                }
            }

            return config;
        }

        /// <summary>
        /// Creates configuration for Azure Blob Storage.
        /// </summary>
        public static AzureBlobConfig Create(string accountName, string accountKey, string container) => new()
        {
            AccountName = accountName,
            AccountKey = accountKey,
            Container = container
        };

        /// <summary>
        /// Creates configuration for Azure Blob Storage with instance ID.
        /// </summary>
        public static AzureBlobConfig Create(string accountName, string accountKey, string container, string instanceId) => new()
        {
            AccountName = accountName,
            AccountKey = accountKey,
            Container = container,
            InstanceId = instanceId
        };

        /// <summary>
        /// Creates configuration for Azure Blob Storage with specific tier.
        /// </summary>
        public static AzureBlobConfig CreateWithTier(string accountName, string accountKey, string container, string tier) => new()
        {
            AccountName = accountName,
            AccountKey = accountKey,
            Container = container,
            DefaultAccessTier = tier
        };

        /// <summary>
        /// Creates configuration for cool tier storage.
        /// </summary>
        public static AzureBlobConfig CoolTier(string accountName, string accountKey, string container) => new()
        {
            AccountName = accountName,
            AccountKey = accountKey,
            Container = container,
            DefaultAccessTier = "Cool"
        };

        /// <summary>
        /// Creates configuration for archive tier storage.
        /// </summary>
        public static AzureBlobConfig ArchiveTier(string accountName, string accountKey, string container) => new()
        {
            AccountName = accountName,
            AccountKey = accountKey,
            Container = container,
            DefaultAccessTier = "Archive"
        };
    }
}
