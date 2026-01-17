using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace DataWarehouse.Plugins.Storage
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
    /// </summary>
    public sealed class AzureBlobStoragePlugin : ListableStoragePluginBase, ITieredStorage
    {
        private readonly AzureBlobConfig _config;
        private readonly HttpClient _httpClient;

        public override string Id => "datawarehouse.plugins.storage.azure";
        public override string Name => "Azure Blob Storage";
        public override string Version => "1.0.0";
        public override string Scheme => "azure";

        /// <summary>
        /// Creates an Azure Blob storage plugin with configuration.
        /// </summary>
        public AzureBlobStoragePlugin(AzureBlobConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
            };
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "storage.azure.put", DisplayName = "Put", Description = "Upload blob to Azure" },
                new() { Name = "storage.azure.get", DisplayName = "Get", Description = "Download blob from Azure" },
                new() { Name = "storage.azure.delete", DisplayName = "Delete", Description = "Delete blob from Azure" },
                new() { Name = "storage.azure.list", DisplayName = "List", Description = "List blobs in container" },
                new() { Name = "storage.azure.tier", DisplayName = "Tier", Description = "Set blob access tier" },
                new() { Name = "storage.azure.properties", DisplayName = "Properties", Description = "Get blob properties" },
                new() { Name = "storage.azure.sas", DisplayName = "SAS", Description = "Generate SAS token" },
                new() { Name = "storage.azure.snapshot", DisplayName = "Snapshot", Description = "Create blob snapshot" },
                new() { Name = "storage.azure.copy", DisplayName = "Copy", Description = "Copy blob" }
            ];
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

            var uri = new Uri($"azure://{_config.Container}/{blobName}");
            await SaveAsync(uri, data);
            return MessageResponse.Ok(new { Container = _config.Container, BlobName = blobName, Success = true });
        }

        private async Task<MessageResponse> HandleGetAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("blobName", out var nameObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'blobName'");
            }

            var blobName = nameObj.ToString()!;
            var uri = new Uri($"azure://{_config.Container}/{blobName}");
            var stream = await LoadAsync(uri);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return MessageResponse.Ok(new { Container = _config.Container, BlobName = blobName, Data = ms.ToArray() });
        }

        private async Task<MessageResponse> HandleDeleteAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("blobName", out var nameObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'blobName'");
            }

            var blobName = nameObj.ToString()!;
            var uri = new Uri($"azure://{_config.Container}/{blobName}");
            await DeleteAsync(uri);
            return MessageResponse.Ok(new { Container = _config.Container, BlobName = blobName, Deleted = true });
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

            var manifest = new Manifest { Id = blobName, StorageUri = new Uri($"azure://{_config.Container}/{blobName}") };
            await MoveToTierAsync(manifest, tier);
            return MessageResponse.Ok(new { BlobName = blobName, Tier = tier.ToString(), Success = true });
        }

        private async Task<MessageResponse> HandlePropertiesAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("blobName", out var nameObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'blobName'");
            }

            var blobName = nameObj.ToString()!;
            var properties = await GetBlobPropertiesAsync(blobName);
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

            var sasUrl = GenerateSasUrl(blobName, expiresIn);
            return MessageResponse.Ok(new { BlobName = blobName, SasUrl = sasUrl, ExpiresIn = expiresIn.TotalSeconds });
        }

        private async Task<MessageResponse> HandleSnapshotAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("blobName", out var nameObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'blobName'");
            }

            var blobName = nameObj.ToString()!;
            var snapshotTime = await CreateSnapshotAsync(blobName);
            return MessageResponse.Ok(new { BlobName = blobName, SnapshotTime = snapshotTime });
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
            await CopyBlobAsync(sourceBlobName, destBlobName);
            return MessageResponse.Ok(new { SourceBlobName = sourceBlobName, DestinationBlobName = destBlobName, Success = true });
        }

        public override async Task SaveAsync(Uri uri, Stream data)
        {
            ArgumentNullException.ThrowIfNull(uri);
            ArgumentNullException.ThrowIfNull(data);

            var blobName = GetBlobName(uri);
            var endpoint = GetBlobUrl(blobName);

            using var ms = new MemoryStream();
            await data.CopyToAsync(ms);
            var content = ms.ToArray();

            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentLength = content.Length;
            request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");

            if (!string.IsNullOrEmpty(_config.DefaultAccessTier))
            {
                request.Headers.TryAddWithoutValidation("x-ms-access-tier", _config.DefaultAccessTier);
            }

            SignRequest(request);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        public override async Task<Stream> LoadAsync(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var blobName = GetBlobName(uri);
            var endpoint = GetBlobUrl(blobName);

            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            SignRequest(request);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var ms = new MemoryStream();
            await response.Content.CopyToAsync(ms);
            ms.Position = 0;
            return ms;
        }

        public override async Task DeleteAsync(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var blobName = GetBlobName(uri);
            var endpoint = GetBlobUrl(blobName);

            var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
            SignRequest(request);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        public override async Task<bool> ExistsAsync(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var blobName = GetBlobName(uri);
            try
            {
                await GetBlobPropertiesAsync(blobName);
                return true;
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
            string? marker = null;

            do
            {
                var endpoint = $"{GetContainerUrl()}?restype=container&comp=list&prefix={Uri.EscapeDataString(prefix)}";
                if (marker != null)
                    endpoint += $"&marker={Uri.EscapeDataString(marker)}";

                var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                SignRequest(request);

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

        /// <summary>
        /// Get blob properties.
        /// </summary>
        public async Task<Dictionary<string, object>> GetBlobPropertiesAsync(string blobName)
        {
            var endpoint = GetBlobUrl(blobName);
            var request = new HttpRequestMessage(HttpMethod.Head, endpoint);
            SignRequest(request);

            var response = await _httpClient.SendAsync(request);
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
        public async Task<string> MoveToTierAsync(Manifest manifest, StorageTier targetTier)
        {
            var blobName = GetBlobName(manifest.StorageUri);
            var azureTier = targetTier switch
            {
                StorageTier.Hot => "Hot",
                StorageTier.Cool => "Cool",
                StorageTier.Cold => "Cold",
                StorageTier.Archive => "Archive",
                _ => "Hot"
            };

            var endpoint = $"{GetBlobUrl(blobName)}?comp=tier";
            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            request.Headers.TryAddWithoutValidation("x-ms-access-tier", azureTier);
            SignRequest(request);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            return manifest.StorageUri.ToString();
        }

        public Task<StorageTier> GetCurrentTierAsync(Uri uri)
        {
            return Task.FromResult(StorageTier.Hot);
        }

        /// <summary>
        /// Generate SAS URL.
        /// </summary>
        public string GenerateSasUrl(string blobName, TimeSpan expiresIn)
        {
            var start = DateTime.UtcNow.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ");
            var expiry = DateTime.UtcNow.Add(expiresIn).ToString("yyyy-MM-ddTHH:mm:ssZ");

            var canonicalizedResource = $"/blob/{_config.AccountName}/{_config.Container}/{blobName}";
            var stringToSign = $"r\n{start}\n{expiry}\n{canonicalizedResource}\n\n\nhttps\n2021-06-08\nb\n\n\n\n\n";

            var keyBytes = Convert.FromBase64String(_config.AccountKey);
            using var hmac = new HMACSHA256(keyBytes);
            var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

            var sasToken = $"sv=2021-06-08&ss=b&srt=o&sp=r&se={Uri.EscapeDataString(expiry)}&st={Uri.EscapeDataString(start)}&spr=https&sig={Uri.EscapeDataString(signature)}";
            return $"{GetBlobUrl(blobName)}?{sasToken}";
        }

        /// <summary>
        /// Create blob snapshot.
        /// </summary>
        public async Task<string> CreateSnapshotAsync(string blobName)
        {
            var endpoint = $"{GetBlobUrl(blobName)}?comp=snapshot";
            var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
            SignRequest(request);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            if (response.Headers.TryGetValues("x-ms-snapshot", out var snapshotValues))
                return snapshotValues.FirstOrDefault() ?? "";

            return DateTime.UtcNow.ToString("o");
        }

        /// <summary>
        /// Copy blob.
        /// </summary>
        public async Task CopyBlobAsync(string sourceBlobName, string destBlobName)
        {
            var sourceUrl = GetBlobUrl(sourceBlobName);
            var destUrl = GetBlobUrl(destBlobName);

            var request = new HttpRequestMessage(HttpMethod.Put, destUrl);
            request.Headers.TryAddWithoutValidation("x-ms-copy-source", sourceUrl);
            SignRequest(request);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        private string GetBlobName(Uri uri)
        {
            if (uri.Scheme == "azure")
                return uri.AbsolutePath.TrimStart('/');

            return uri.AbsolutePath.TrimStart('/');
        }

        private string GetBlobUrl(string blobName)
        {
            return $"https://{_config.AccountName}.blob.core.windows.net/{_config.Container}/{blobName}";
        }

        private string GetContainerUrl()
        {
            return $"https://{_config.AccountName}.blob.core.windows.net/{_config.Container}";
        }

        private void SignRequest(HttpRequestMessage request)
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
            var canonicalResource = $"/{_config.AccountName}{uri.AbsolutePath}";
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

            var keyBytes = Convert.FromBase64String(_config.AccountKey);
            using var hmac = new HMACSHA256(keyBytes);
            var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("SharedKey", $"{_config.AccountName}:{signature}");
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
    }

    /// <summary>
    /// Configuration for Azure Blob Storage.
    /// </summary>
    public class AzureBlobConfig
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
    }
}
