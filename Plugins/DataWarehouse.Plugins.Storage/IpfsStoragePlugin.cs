using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Text.Json;

namespace DataWarehouse.Plugins.Storage
{
    /// <summary>
    /// IPFS (InterPlanetary File System) storage plugin.
    ///
    /// Features:
    /// - Content-addressed storage using CIDs
    /// - Integration with local IPFS node or gateway
    /// - Support for pinning services
    /// - IPNS for mutable references
    /// - MFS (Mutable File System) support
    /// - Automatic chunking for large files
    /// - DAG operations support
    ///
    /// Message Commands:
    /// - storage.ipfs.add: Add content to IPFS (returns CID)
    /// - storage.ipfs.cat: Retrieve content by CID
    /// - storage.ipfs.pin: Pin content to local node
    /// - storage.ipfs.unpin: Unpin content
    /// - storage.ipfs.ls: List pinned content
    /// - storage.ipfs.publish: Publish to IPNS
    /// - storage.ipfs.resolve: Resolve IPNS name
    /// - storage.ipfs.stats: Get node statistics
    /// </summary>
    public sealed class IpfsStoragePlugin : ListableStoragePluginBase
    {
        private readonly IpfsConfig _config;
        private readonly HttpClient _httpClient;

        public override string Id => "datawarehouse.plugins.storage.ipfs";
        public override string Name => "IPFS Storage";
        public override string Version => "1.0.0";
        public override string Scheme => "ipfs";

        /// <summary>
        /// Creates an IPFS storage plugin with optional configuration.
        /// </summary>
        public IpfsStoragePlugin(IpfsConfig? config = null)
        {
            _config = config ?? new IpfsConfig();
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_config.ApiEndpoint),
                Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
            };
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "storage.ipfs.add", DisplayName = "Add", Description = "Add content to IPFS and get CID" },
                new() { Name = "storage.ipfs.cat", DisplayName = "Cat", Description = "Retrieve content by CID" },
                new() { Name = "storage.ipfs.pin", DisplayName = "Pin", Description = "Pin content to local node" },
                new() { Name = "storage.ipfs.unpin", DisplayName = "Unpin", Description = "Unpin content from node" },
                new() { Name = "storage.ipfs.ls", DisplayName = "List", Description = "List pinned content" },
                new() { Name = "storage.ipfs.publish", DisplayName = "Publish", Description = "Publish to IPNS" },
                new() { Name = "storage.ipfs.resolve", DisplayName = "Resolve", Description = "Resolve IPNS name" },
                new() { Name = "storage.ipfs.stats", DisplayName = "Stats", Description = "Get IPFS node statistics" },
                new() { Name = "storage.ipfs.dag.put", DisplayName = "DAG Put", Description = "Add DAG node" },
                new() { Name = "storage.ipfs.dag.get", DisplayName = "DAG Get", Description = "Get DAG node" }
            ];
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Description"] = "Content-addressed storage using IPFS network.";
            metadata["ApiEndpoint"] = _config.ApiEndpoint;
            metadata["GatewayUrl"] = _config.GatewayUrl;
            metadata["AutoPin"] = _config.AutoPin;
            metadata["ContentAddressed"] = true;
            metadata["Distributed"] = true;
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
                "storage.ipfs.add" => await HandleAddAsync(message),
                "storage.ipfs.cat" => await HandleCatAsync(message),
                "storage.ipfs.pin" => await HandlePinAsync(message),
                "storage.ipfs.unpin" => await HandleUnpinAsync(message),
                "storage.ipfs.publish" => await HandlePublishAsync(message),
                "storage.ipfs.resolve" => await HandleResolveAsync(message),
                "storage.ipfs.stats" => await HandleStatsAsync(message),
                "storage.ipfs.dag.put" => await HandleDagPutAsync(message),
                "storage.ipfs.dag.get" => await HandleDagGetAsync(message),
                _ => null
            };
        }

        private async Task<MessageResponse> HandleAddAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("data", out var dataObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'data'");
            }

            var data = dataObj switch
            {
                Stream s => s,
                byte[] b => new MemoryStream(b),
                string str => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(str)),
                _ => throw new ArgumentException("Data must be Stream, byte[], or string")
            };

            var cid = await AddAsync(data);
            return MessageResponse.Ok(new { Cid = cid, Uri = $"ipfs://{cid}" });
        }

        private async Task<MessageResponse> HandleCatAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("cid", out var cidObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'cid'");
            }

            var cid = cidObj.ToString()!;
            var stream = await CatAsync(cid);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return MessageResponse.Ok(new { Cid = cid, Data = ms.ToArray() });
        }

        private async Task<MessageResponse> HandlePinAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("cid", out var cidObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'cid'");
            }

            var cid = cidObj.ToString()!;
            await PinAsync(cid);
            return MessageResponse.Ok(new { Cid = cid, Pinned = true });
        }

        private async Task<MessageResponse> HandleUnpinAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("cid", out var cidObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'cid'");
            }

            var cid = cidObj.ToString()!;
            await UnpinAsync(cid);
            return MessageResponse.Ok(new { Cid = cid, Unpinned = true });
        }

        private async Task<MessageResponse> HandlePublishAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("cid", out var cidObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'cid'");
            }

            var cid = cidObj.ToString()!;
            var ipnsName = await PublishAsync(cid);
            return MessageResponse.Ok(new { Cid = cid, IpnsName = ipnsName });
        }

        private async Task<MessageResponse> HandleResolveAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("name", out var nameObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'name'");
            }

            var name = nameObj.ToString()!;
            var cid = await ResolveAsync(name);
            return MessageResponse.Ok(new { Name = name, Cid = cid });
        }

        private async Task<MessageResponse> HandleStatsAsync(PluginMessage message)
        {
            var stats = await GetStatsAsync();
            return MessageResponse.Ok(stats);
        }

        private async Task<MessageResponse> HandleDagPutAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("data", out var dataObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'data'");
            }

            var json = JsonSerializer.Serialize(dataObj);
            var cid = await DagPutAsync(json);
            return MessageResponse.Ok(new { Cid = cid });
        }

        private async Task<MessageResponse> HandleDagGetAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("cid", out var cidObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'cid'");
            }

            var cid = cidObj.ToString()!;
            var data = await DagGetAsync(cid);
            return MessageResponse.Ok(new { Cid = cid, Data = data });
        }

        public override async Task SaveAsync(Uri uri, Stream data)
        {
            ArgumentNullException.ThrowIfNull(uri);
            ArgumentNullException.ThrowIfNull(data);

            // For IPFS, saving returns a CID - we ignore the provided URI path
            // The actual storage location is determined by content hash
            await AddAsync(data);
        }

        public override async Task<Stream> LoadAsync(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var cid = ExtractCid(uri);
            return await CatAsync(cid);
        }

        public override async Task DeleteAsync(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var cid = ExtractCid(uri);
            await UnpinAsync(cid);
        }

        public override async Task<bool> ExistsAsync(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var cid = ExtractCid(uri);
            try
            {
                // Try to stat the object
                var response = await _httpClient.PostAsync($"/api/v0/object/stat?arg={cid}", null);
                return response.IsSuccessStatusCode;
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
            // List pinned items
            var response = await _httpClient.PostAsync("/api/v0/pin/ls?type=all", null, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<IpfsPinLsResult>(json);

            if (result?.Keys != null)
            {
                foreach (var kvp in result.Keys)
                {
                    if (ct.IsCancellationRequested)
                        yield break;

                    if (string.IsNullOrEmpty(prefix) || kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var uri = new Uri($"ipfs://{kvp.Key}");
                        // Size is not directly available from pin ls, would need separate stat call
                        yield return new StorageListItem(uri, 0);
                    }

                    await Task.Yield();
                }
            }
        }

        /// <summary>
        /// Add content to IPFS and return the CID.
        /// </summary>
        public async Task<string> AddAsync(Stream data)
        {
            using var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(data);
            content.Add(streamContent, "file", "data");

            var queryParams = new List<string>();
            if (_config.AutoPin) queryParams.Add("pin=true");
            if (_config.Chunker != null) queryParams.Add($"chunker={_config.Chunker}");
            if (_config.CidVersion.HasValue) queryParams.Add($"cid-version={_config.CidVersion}");

            var query = queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "";

            var response = await _httpClient.PostAsync($"/api/v0/add{query}", content);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<IpfsAddResult>(json);

            return result?.Hash ?? throw new InvalidOperationException("Failed to get CID from IPFS");
        }

        /// <summary>
        /// Retrieve content by CID.
        /// </summary>
        public async Task<Stream> CatAsync(string cid)
        {
            // Try gateway first for better performance
            if (!string.IsNullOrEmpty(_config.GatewayUrl))
            {
                try
                {
                    var gatewayClient = new HttpClient { Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds) };
                    var gatewayResponse = await gatewayClient.GetAsync($"{_config.GatewayUrl}/ipfs/{cid}");
                    if (gatewayResponse.IsSuccessStatusCode)
                    {
                        var ms = new MemoryStream();
                        await gatewayResponse.Content.CopyToAsync(ms);
                        ms.Position = 0;
                        return ms;
                    }
                }
                catch
                {
                    // Fall back to API
                }
            }

            // Use API
            var response = await _httpClient.PostAsync($"/api/v0/cat?arg={cid}", null);
            response.EnsureSuccessStatusCode();

            var resultMs = new MemoryStream();
            await response.Content.CopyToAsync(resultMs);
            resultMs.Position = 0;
            return resultMs;
        }

        /// <summary>
        /// Pin content to local node.
        /// </summary>
        public async Task PinAsync(string cid)
        {
            var response = await _httpClient.PostAsync($"/api/v0/pin/add?arg={cid}", null);
            response.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// Unpin content from node.
        /// </summary>
        public async Task UnpinAsync(string cid)
        {
            var response = await _httpClient.PostAsync($"/api/v0/pin/rm?arg={cid}", null);
            response.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// Publish CID to IPNS.
        /// </summary>
        public async Task<string> PublishAsync(string cid)
        {
            var response = await _httpClient.PostAsync($"/api/v0/name/publish?arg=/ipfs/{cid}", null);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<IpnsPublishResult>(json);
            return result?.Name ?? throw new InvalidOperationException("Failed to publish to IPNS");
        }

        /// <summary>
        /// Resolve IPNS name to CID.
        /// </summary>
        public async Task<string> ResolveAsync(string name)
        {
            var response = await _httpClient.PostAsync($"/api/v0/name/resolve?arg={name}", null);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<IpnsResolveResult>(json);
            var path = result?.Path ?? throw new InvalidOperationException("Failed to resolve IPNS name");

            // Extract CID from path (e.g., /ipfs/Qm...)
            return path.Replace("/ipfs/", "");
        }

        /// <summary>
        /// Get IPFS node statistics.
        /// </summary>
        public async Task<object> GetStatsAsync()
        {
            var response = await _httpClient.PostAsync("/api/v0/stats/repo", null);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<object>(json) ?? new { };
        }

        /// <summary>
        /// Add DAG node.
        /// </summary>
        public async Task<string> DagPutAsync(string json)
        {
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("/api/v0/dag/put?format=dag-json&input-codec=dag-json", content);
            response.EnsureSuccessStatusCode();

            var resultJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<IpfsDagPutResult>(resultJson);
            return result?.Cid?.Value ?? throw new InvalidOperationException("Failed to add DAG node");
        }

        /// <summary>
        /// Get DAG node.
        /// </summary>
        public async Task<object?> DagGetAsync(string cid)
        {
            var response = await _httpClient.PostAsync($"/api/v0/dag/get?arg={cid}", null);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<object>(json);
        }

        private static string ExtractCid(Uri uri)
        {
            // Handle ipfs:// scheme
            if (uri.Scheme == "ipfs")
                return uri.Host + uri.AbsolutePath.TrimStart('/');

            // Handle /ipfs/CID format
            var path = uri.AbsolutePath;
            if (path.StartsWith("/ipfs/"))
                return path.Substring(6);

            return path.TrimStart('/');
        }

        // JSON response models
        private sealed class IpfsAddResult
        {
            public string? Name { get; set; }
            public string? Hash { get; set; }
            public string? Size { get; set; }
        }

        private sealed class IpfsPinLsResult
        {
            public Dictionary<string, IpfsPinInfo>? Keys { get; set; }
        }

        private sealed class IpfsPinInfo
        {
            public string? Type { get; set; }
        }

        private sealed class IpnsPublishResult
        {
            public string? Name { get; set; }
            public string? Value { get; set; }
        }

        private sealed class IpnsResolveResult
        {
            public string? Path { get; set; }
        }

        private sealed class IpfsDagPutResult
        {
            public IpfsCidRef? Cid { get; set; }
        }

        private sealed class IpfsCidRef
        {
            public string? Value { get; set; }
        }
    }

    /// <summary>
    /// Configuration for IPFS storage.
    /// </summary>
    public class IpfsConfig
    {
        /// <summary>
        /// IPFS API endpoint (default: local node).
        /// </summary>
        public string ApiEndpoint { get; set; } = "http://127.0.0.1:5001";

        /// <summary>
        /// IPFS Gateway URL for reads (optional, faster for public content).
        /// </summary>
        public string? GatewayUrl { get; set; } = "https://ipfs.io";

        /// <summary>
        /// Automatically pin added content.
        /// </summary>
        public bool AutoPin { get; set; } = true;

        /// <summary>
        /// Chunker algorithm (e.g., "size-262144", "rabin-262144-524288-1048576").
        /// </summary>
        public string? Chunker { get; set; }

        /// <summary>
        /// CID version (0 or 1).
        /// </summary>
        public int? CidVersion { get; set; }

        /// <summary>
        /// Request timeout in seconds.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 300;

        /// <summary>
        /// Creates configuration for local IPFS node.
        /// </summary>
        public static IpfsConfig Local => new();

        /// <summary>
        /// Creates configuration using Infura IPFS.
        /// </summary>
        public static IpfsConfig Infura(string projectId, string projectSecret) => new()
        {
            ApiEndpoint = $"https://ipfs.infura.io:5001",
            GatewayUrl = $"https://{projectId}.ipfs.infura-ipfs.io"
        };

        /// <summary>
        /// Creates configuration using Pinata.
        /// </summary>
        public static IpfsConfig Pinata(string apiKey, string secretKey) => new()
        {
            ApiEndpoint = "https://api.pinata.cloud",
            GatewayUrl = "https://gateway.pinata.cloud"
        };
    }
}
