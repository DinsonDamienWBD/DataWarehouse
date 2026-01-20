using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Infrastructure;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Storage;
using DataWarehouse.SDK.Utilities;
using System.Text.Json;

namespace DataWarehouse.Plugins.IpfsStorage
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
    /// - Multi-instance support with role-based selection
    /// - TTL-based caching support
    /// - Document indexing and search
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
    /// - storage.instance.*: Multi-instance management
    /// </summary>
    public sealed class IpfsStoragePlugin : HybridStoragePluginBase<IpfsConfig>
    {
        private readonly HttpClient _httpClient;

        public override string Id => "datawarehouse.plugins.storage.ipfs";
        public override string Name => "IPFS Storage";
        public override string Version => "2.0.0";
        public override string Scheme => "ipfs";
        public override string StorageCategory => "Distributed";

        /// <summary>
        /// Creates an IPFS storage plugin with optional configuration.
        /// </summary>
        public IpfsStoragePlugin(IpfsConfig? config = null)
            : base(config ?? new IpfsConfig())
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_config.ApiEndpoint),
                Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
            };
        }

        /// <summary>
        /// Creates a connection for the given configuration.
        /// </summary>
        protected override Task<object> CreateConnectionAsync(IpfsConfig config)
        {
            var client = new HttpClient
            {
                BaseAddress = new Uri(config.ApiEndpoint),
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            };

            return Task.FromResult<object>(new IpfsConnection
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
                new PluginCapabilityDescriptor { Name = "storage.ipfs.add", DisplayName = "Add", Description = "Add content to IPFS and get CID" },
                new PluginCapabilityDescriptor { Name = "storage.ipfs.cat", DisplayName = "Cat", Description = "Retrieve content by CID" },
                new PluginCapabilityDescriptor { Name = "storage.ipfs.pin", DisplayName = "Pin", Description = "Pin content to local node" },
                new PluginCapabilityDescriptor { Name = "storage.ipfs.unpin", DisplayName = "Unpin", Description = "Unpin content from node" },
                new PluginCapabilityDescriptor { Name = "storage.ipfs.ls", DisplayName = "List", Description = "List pinned content" },
                new PluginCapabilityDescriptor { Name = "storage.ipfs.publish", DisplayName = "Publish", Description = "Publish to IPNS" },
                new PluginCapabilityDescriptor { Name = "storage.ipfs.resolve", DisplayName = "Resolve", Description = "Resolve IPNS name" },
                new PluginCapabilityDescriptor { Name = "storage.ipfs.stats", DisplayName = "Stats", Description = "Get IPFS node statistics" },
                new PluginCapabilityDescriptor { Name = "storage.ipfs.dag.put", DisplayName = "DAG Put", Description = "Add DAG node" },
                new PluginCapabilityDescriptor { Name = "storage.ipfs.dag.get", DisplayName = "DAG Get", Description = "Get DAG node" }
            });
            return capabilities;
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Description"] = "Content-addressed storage using IPFS network.";
            metadata["ApiEndpoint"] = _config.ApiEndpoint;
            if (_config.GatewayUrl != null)
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

            // If not handled, delegate to base for multi-instance management
            if (response == null)
            {
                await base.OnMessageAsync(message);
            }
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

            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var cid = await AddAsync(data, instanceId);
            return MessageResponse.Ok(new { Cid = cid, Uri = $"ipfs://{cid}", InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleCatAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("cid", out var cidObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'cid'");
            }

            var cid = cidObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var stream = await CatAsync(cid, instanceId);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return MessageResponse.Ok(new { Cid = cid, Data = ms.ToArray(), InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandlePinAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("cid", out var cidObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'cid'");
            }

            var cid = cidObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            await PinAsync(cid, instanceId);
            return MessageResponse.Ok(new { Cid = cid, Pinned = true, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleUnpinAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("cid", out var cidObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'cid'");
            }

            var cid = cidObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            await UnpinAsync(cid, instanceId);
            return MessageResponse.Ok(new { Cid = cid, Unpinned = true, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandlePublishAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("cid", out var cidObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'cid'");
            }

            var cid = cidObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var ipnsName = await PublishAsync(cid, instanceId);
            return MessageResponse.Ok(new { Cid = cid, IpnsName = ipnsName, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleResolveAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("name", out var nameObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'name'");
            }

            var name = nameObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var cid = await ResolveAsync(name, instanceId);
            return MessageResponse.Ok(new { Name = name, Cid = cid, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleStatsAsync(PluginMessage message)
        {
            string? instanceId = null;
            if (message.Payload is Dictionary<string, object> payload)
            {
                instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;
            }

            var stats = await GetStatsAsync(instanceId);
            return MessageResponse.Ok(stats);
        }

        private async Task<MessageResponse> HandleDagPutAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("data", out var dataObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'data'");
            }

            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var json = JsonSerializer.Serialize(dataObj);
            var cid = await DagPutAsync(json, instanceId);
            return MessageResponse.Ok(new { Cid = cid, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleDagGetAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("cid", out var cidObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'cid'");
            }

            var cid = cidObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var data = await DagGetAsync(cid, instanceId);
            return MessageResponse.Ok(new { Cid = cid, Data = data, InstanceId = instanceId });
        }

        #region Storage Operations with Instance Support

        public async Task SaveAsync(Uri uri, Stream data, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);
            ArgumentNullException.ThrowIfNull(data);

            // For IPFS, saving returns a CID - we ignore the provided URI path
            // The actual storage location is determined by content hash
            var config = GetConfig(instanceId);
            var cid = await AddAsync(data, instanceId);

            // Auto-index if enabled
            if (config.EnableIndexing)
            {
                await IndexDocumentAsync(uri.ToString(), new Dictionary<string, object>
                {
                    ["uri"] = uri.ToString(),
                    ["cid"] = cid,
                    ["ipfsUri"] = $"ipfs://{cid}",
                    ["instanceId"] = instanceId ?? "default"
                });
            }
        }

        public override Task SaveAsync(Uri uri, Stream data) => SaveAsync(uri, data, null);

        public async Task<Stream> LoadAsync(Uri uri, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var cid = ExtractCid(uri);

            // Update last access for caching
            await TouchAsync(uri);

            return await CatAsync(cid, instanceId);
        }

        public override Task<Stream> LoadAsync(Uri uri) => LoadAsync(uri, null);

        public async Task DeleteAsync(Uri uri, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var cid = ExtractCid(uri);
            await UnpinAsync(cid, instanceId);

            // Remove from index
            _ = RemoveFromIndexAsync(uri.ToString());
        }

        public override Task DeleteAsync(Uri uri) => DeleteAsync(uri, null);

        public async Task<bool> ExistsAsync(Uri uri, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var cid = ExtractCid(uri);
            var client = GetHttpClient(instanceId);

            try
            {
                // Try to stat the object
                var response = await client.PostAsync($"/api/v0/object/stat?arg={cid}", null);
                return response.IsSuccessStatusCode;
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

        #endregion

        #region IPFS-Specific Operations

        /// <summary>
        /// Add content to IPFS and return the CID.
        /// </summary>
        public async Task<string> AddAsync(Stream data, string? instanceId = null)
        {
            var config = GetConfig(instanceId);
            var client = GetHttpClient(instanceId);

            using var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(data);
            content.Add(streamContent, "file", "data");

            var queryParams = new List<string>();
            if (config.AutoPin) queryParams.Add("pin=true");
            if (config.Chunker != null) queryParams.Add($"chunker={config.Chunker}");
            if (config.CidVersion.HasValue) queryParams.Add($"cid-version={config.CidVersion}");

            var query = queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "";

            var response = await client.PostAsync($"/api/v0/add{query}", content);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<IpfsAddResult>(json);

            return result?.Hash ?? throw new InvalidOperationException("Failed to get CID from IPFS");
        }

        /// <summary>
        /// Retrieve content by CID.
        /// </summary>
        public async Task<Stream> CatAsync(string cid, string? instanceId = null)
        {
            var config = GetConfig(instanceId);
            var client = GetHttpClient(instanceId);

            // Try gateway first for better performance
            if (!string.IsNullOrEmpty(config.GatewayUrl))
            {
                try
                {
                    var gatewayClient = new HttpClient { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds) };
                    var gatewayResponse = await gatewayClient.GetAsync($"{config.GatewayUrl}/ipfs/{cid}");
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
            var response = await client.PostAsync($"/api/v0/cat?arg={cid}", null);
            response.EnsureSuccessStatusCode();

            var resultMs = new MemoryStream();
            await response.Content.CopyToAsync(resultMs);
            resultMs.Position = 0;
            return resultMs;
        }

        /// <summary>
        /// Pin content to local node.
        /// </summary>
        public async Task PinAsync(string cid, string? instanceId = null)
        {
            var client = GetHttpClient(instanceId);
            var response = await client.PostAsync($"/api/v0/pin/add?arg={cid}", null);
            response.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// Unpin content from node.
        /// </summary>
        public async Task UnpinAsync(string cid, string? instanceId = null)
        {
            var client = GetHttpClient(instanceId);
            var response = await client.PostAsync($"/api/v0/pin/rm?arg={cid}", null);
            response.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// Publish CID to IPNS.
        /// </summary>
        public async Task<string> PublishAsync(string cid, string? instanceId = null)
        {
            var client = GetHttpClient(instanceId);
            var response = await client.PostAsync($"/api/v0/name/publish?arg=/ipfs/{cid}", null);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<IpnsPublishResult>(json);
            return result?.Name ?? throw new InvalidOperationException("Failed to publish to IPNS");
        }

        /// <summary>
        /// Resolve IPNS name to CID.
        /// </summary>
        public async Task<string> ResolveAsync(string name, string? instanceId = null)
        {
            var client = GetHttpClient(instanceId);
            var response = await client.PostAsync($"/api/v0/name/resolve?arg={name}", null);
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
        public async Task<object> GetStatsAsync(string? instanceId = null)
        {
            var client = GetHttpClient(instanceId);
            var response = await client.PostAsync("/api/v0/stats/repo", null);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<object>(json) ?? new { };
        }

        /// <summary>
        /// Add DAG node.
        /// </summary>
        public async Task<string> DagPutAsync(string json, string? instanceId = null)
        {
            var client = GetHttpClient(instanceId);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/api/v0/dag/put?format=dag-json&input-codec=dag-json", content);
            response.EnsureSuccessStatusCode();

            var resultJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<IpfsDagPutResult>(resultJson);
            return result?.Cid?.Value ?? throw new InvalidOperationException("Failed to add DAG node");
        }

        /// <summary>
        /// Get DAG node.
        /// </summary>
        public async Task<object?> DagGetAsync(string cid, string? instanceId = null)
        {
            var client = GetHttpClient(instanceId);
            var response = await client.PostAsync($"/api/v0/dag/get?arg={cid}", null);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<object>(json);
        }

        #endregion

        #region Helper Methods

        private IpfsConfig GetConfig(string? instanceId)
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
            if (instance?.Connection is IpfsConnection conn)
                return conn.HttpClient;

            return _httpClient;
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

        #endregion

        #region JSON Response Models

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

        #endregion
    }

    /// <summary>
    /// Internal connection wrapper for IPFS instances.
    /// </summary>
    internal class IpfsConnection : IDisposable
    {
        public required IpfsConfig Config { get; init; }
        public required HttpClient HttpClient { get; init; }

        public void Dispose()
        {
            HttpClient?.Dispose();
        }
    }

    /// <summary>
    /// Configuration for IPFS storage.
    /// </summary>
    public class IpfsConfig : StorageConfigBase
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
        /// Creates configuration for local IPFS node with instance ID.
        /// </summary>
        public static IpfsConfig LocalWithInstance(string instanceId) => new() { InstanceId = instanceId };

        /// <summary>
        /// Creates configuration using Infura IPFS.
        /// </summary>
        public static IpfsConfig Infura(string projectId, string projectSecret) => new()
        {
            ApiEndpoint = $"https://ipfs.infura.io:5001",
            GatewayUrl = $"https://{projectId}.ipfs.infura-ipfs.io"
        };

        /// <summary>
        /// Creates configuration using Infura IPFS with instance ID.
        /// </summary>
        public static IpfsConfig Infura(string projectId, string projectSecret, string instanceId) => new()
        {
            ApiEndpoint = $"https://ipfs.infura.io:5001",
            GatewayUrl = $"https://{projectId}.ipfs.infura-ipfs.io",
            InstanceId = instanceId
        };

        /// <summary>
        /// Creates configuration using Pinata.
        /// </summary>
        public static IpfsConfig Pinata(string apiKey, string secretKey) => new()
        {
            ApiEndpoint = "https://api.pinata.cloud",
            GatewayUrl = "https://gateway.pinata.cloud"
        };

        /// <summary>
        /// Creates configuration using Pinata with instance ID.
        /// </summary>
        public static IpfsConfig Pinata(string apiKey, string secretKey, string instanceId) => new()
        {
            ApiEndpoint = "https://api.pinata.cloud",
            GatewayUrl = "https://gateway.pinata.cloud",
            InstanceId = instanceId
        };
    }
}
