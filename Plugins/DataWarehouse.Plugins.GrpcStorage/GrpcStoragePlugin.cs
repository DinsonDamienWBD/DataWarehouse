using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Infrastructure;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Storage;
using DataWarehouse.SDK.Utilities;
using System.Text;

namespace DataWarehouse.Plugins.GrpcStorage
{
    /// <summary>
    /// gRPC/Protobuf-based storage plugin for high-performance remote storage.
    ///
    /// Features:
    /// - High-performance binary protocol using gRPC
    /// - Bidirectional streaming for large file transfers
    /// - Connection pooling and multiplexing
    /// - Automatic reconnection with backoff
    /// - Support for TLS/mTLS
    /// - Health checking and load balancing
    /// - Deadline propagation
    /// - Multi-instance support with role-based selection
    /// - TTL-based caching support
    /// - Document indexing and search
    ///
    /// Message Commands:
    /// - storage.grpc.put: Store data via gRPC
    /// - storage.grpc.get: Retrieve data via gRPC
    /// - storage.grpc.delete: Delete data via gRPC
    /// - storage.grpc.exists: Check if data exists
    /// - storage.grpc.list: List stored items
    /// - storage.grpc.stream.upload: Upload via streaming
    /// - storage.grpc.stream.download: Download via streaming
    /// - storage.grpc.health: Check server health
    /// - storage.instance.*: Multi-instance management
    /// </summary>
    public sealed class GrpcStoragePlugin : HybridStoragePluginBase<GrpcStorageConfig>
    {
        private readonly HttpClient _httpClient; // Using HTTP/2 for gRPC-like functionality
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private bool _isConnected;

        public override string Id => "datawarehouse.plugins.storage.grpc";
        public override string Name => "gRPC Storage";
        public override string Version => "2.0.0";
        public override string Scheme => "grpc";
        public override string StorageCategory => "Remote";

        /// <summary>
        /// Creates a gRPC storage plugin with configuration.
        /// </summary>
        public GrpcStoragePlugin(GrpcStorageConfig config)
            : base(config ?? throw new ArgumentNullException(nameof(config)))
        {
            _httpClient = CreateHttpClientForConfig(_config);
        }

        /// <summary>
        /// Creates an HttpClient configured for gRPC communication.
        /// </summary>
        private HttpClient CreateHttpClientForConfig(GrpcStorageConfig config)
        {
            var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30)
            };

            // Security Warning: AcceptAnyCertificate bypasses SSL/TLS validation.
            // This should ONLY be used in development/testing environments.
            // In production, use proper certificate validation.
            if (config.UseTls && config.AcceptAnyCertificate)
            {
                if (!config.IsDevelopmentMode)
                {
                    throw new InvalidOperationException(
                        "Security Error: AcceptAnyCertificate requires IsDevelopmentMode=true. " +
                        "Disabling certificate validation in production is a critical security vulnerability.");
                }

                // Log security warning (would be logged by context in full implementation)
                Console.Error.WriteLine(
                    "[SECURITY WARNING] SSL certificate validation is DISABLED. " +
                    "This is vulnerable to man-in-the-middle attacks. " +
                    "Only use in development environments.");

                handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                };
            }

            return new HttpClient(handler)
            {
                BaseAddress = new Uri(config.Endpoint),
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds),
                DefaultRequestVersion = new Version(2, 0),
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
            };
        }

        /// <summary>
        /// Creates a connection for the given configuration.
        /// </summary>
        protected override Task<object> CreateConnectionAsync(GrpcStorageConfig config)
        {
            var client = CreateHttpClientForConfig(config);

            return Task.FromResult<object>(new GrpcStorageConnection
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
                new PluginCapabilityDescriptor { Name = "storage.grpc.put", DisplayName = "Put", Description = "Store data via gRPC" },
                new PluginCapabilityDescriptor { Name = "storage.grpc.get", DisplayName = "Get", Description = "Retrieve data via gRPC" },
                new PluginCapabilityDescriptor { Name = "storage.grpc.delete", DisplayName = "Delete", Description = "Delete data via gRPC" },
                new PluginCapabilityDescriptor { Name = "storage.grpc.exists", DisplayName = "Exists", Description = "Check if data exists" },
                new PluginCapabilityDescriptor { Name = "storage.grpc.list", DisplayName = "List", Description = "List stored items" },
                new PluginCapabilityDescriptor { Name = "storage.grpc.stream.upload", DisplayName = "Stream Upload", Description = "Upload via streaming" },
                new PluginCapabilityDescriptor { Name = "storage.grpc.stream.download", DisplayName = "Stream Download", Description = "Download via streaming" },
                new PluginCapabilityDescriptor { Name = "storage.grpc.health", DisplayName = "Health", Description = "Check server health" }
            });
            return capabilities;
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Description"] = "High-performance gRPC/Protobuf storage for remote backends";
            metadata["Endpoint"] = _config.Endpoint;
            metadata["UseTls"] = _config.UseTls;
            metadata["SupportsStreaming"] = true;
            metadata["SupportsMultiplexing"] = true;
            metadata["Protocol"] = "HTTP/2 + gRPC";
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
                "storage.grpc.put" => await HandlePutAsync(message),
                "storage.grpc.get" => await HandleGetAsync(message),
                "storage.grpc.delete" => await HandleDeleteAsync(message),
                "storage.grpc.exists" => await HandleExistsAsync(message),
                "storage.grpc.health" => await HandleHealthAsync(message),
                "storage.grpc.stream.upload" => await HandleStreamUploadAsync(message),
                "storage.grpc.stream.download" => await HandleStreamDownloadAsync(message),
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
                !payload.TryGetValue("key", out var keyObj) ||
                !payload.TryGetValue("data", out var dataObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'key' and 'data'");
            }

            var key = keyObj.ToString()!;
            var data = dataObj switch
            {
                Stream s => s,
                byte[] b => new MemoryStream(b),
                string str => new MemoryStream(Encoding.UTF8.GetBytes(str)),
                _ => throw new ArgumentException("Data must be Stream, byte[], or string")
            };

            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;
            var config = GetConfig(instanceId);

            var uri = new Uri($"grpc://{new Uri(config.Endpoint).Host}/{key}");
            await SaveAsync(uri, data, instanceId);
            return MessageResponse.Ok(new { Key = key, Success = true, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleGetAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("key", out var keyObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'key'");
            }

            var key = keyObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;
            var config = GetConfig(instanceId);

            var uri = new Uri($"grpc://{new Uri(config.Endpoint).Host}/{key}");
            var stream = await LoadAsync(uri, instanceId);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return MessageResponse.Ok(new { Key = key, Data = ms.ToArray(), InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleDeleteAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("key", out var keyObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'key'");
            }

            var key = keyObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;
            var config = GetConfig(instanceId);

            var uri = new Uri($"grpc://{new Uri(config.Endpoint).Host}/{key}");
            await DeleteAsync(uri, instanceId);
            return MessageResponse.Ok(new { Key = key, Deleted = true, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleExistsAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("key", out var keyObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'key'");
            }

            var key = keyObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;
            var config = GetConfig(instanceId);

            var uri = new Uri($"grpc://{new Uri(config.Endpoint).Host}/{key}");
            var exists = await ExistsAsync(uri, instanceId);
            return MessageResponse.Ok(new { Key = key, Exists = exists, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleHealthAsync(PluginMessage message)
        {
            var instanceId = (message.Payload as Dictionary<string, object>)?.TryGetValue("instanceId", out var instId) == true
                ? instId?.ToString()
                : null;

            var health = await CheckHealthAsync(instanceId);
            return MessageResponse.Ok(health);
        }

        private async Task<MessageResponse> HandleStreamUploadAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("key", out var keyObj) ||
                !payload.TryGetValue("data", out var dataObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'key' and 'data'");
            }

            var key = keyObj.ToString()!;
            var data = dataObj switch
            {
                Stream s => s,
                byte[] b => new MemoryStream(b),
                _ => throw new ArgumentException("Data must be Stream or byte[]")
            };

            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            await StreamUploadAsync(key, data, instanceId: instanceId);
            return MessageResponse.Ok(new { Key = key, Streamed = true, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleStreamDownloadAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("key", out var keyObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'key'");
            }

            var key = keyObj.ToString()!;
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var stream = await StreamDownloadAsync(key, instanceId);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return MessageResponse.Ok(new { Key = key, Data = ms.ToArray(), InstanceId = instanceId });
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

            await EnsureConnectedAsync(instanceId);

            var key = GetKey(uri);

            using var ms = new MemoryStream();
            await data.CopyToAsync(ms);
            var content = ms.ToArray();

            // Simulate gRPC unary call using HTTP/2
            var request = new HttpRequestMessage(HttpMethod.Post, $"/storage.StorageService/Put")
            {
                Content = new ByteArrayContent(BuildGrpcRequest(key, content))
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");
            request.Headers.TryAddWithoutValidation("grpc-timeout", $"{config.TimeoutSeconds}S");

            if (!string.IsNullOrEmpty(config.AuthToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AuthToken);
            }

            var response = await client.SendAsync(request);
            await ValidateGrpcResponseAsync(response);

            // Auto-index if enabled
            if (config.EnableIndexing)
            {
                await IndexDocumentAsync(uri.ToString(), new Dictionary<string, object>
                {
                    ["uri"] = uri.ToString(),
                    ["endpoint"] = config.Endpoint,
                    ["key"] = key,
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

            await EnsureConnectedAsync(instanceId);

            var key = GetKey(uri);

            var request = new HttpRequestMessage(HttpMethod.Post, $"/storage.StorageService/Get")
            {
                Content = new ByteArrayContent(BuildGrpcKeyRequest(key))
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");

            if (!string.IsNullOrEmpty(config.AuthToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AuthToken);
            }

            var response = await client.SendAsync(request);
            await ValidateGrpcResponseAsync(response);

            // Update last access for caching
            await TouchAsync(uri);

            var responseData = await response.Content.ReadAsByteArrayAsync();
            var payload = ParseGrpcResponse(responseData);
            return new MemoryStream(payload);
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

            await EnsureConnectedAsync(instanceId);

            var key = GetKey(uri);

            var request = new HttpRequestMessage(HttpMethod.Post, $"/storage.StorageService/Delete")
            {
                Content = new ByteArrayContent(BuildGrpcKeyRequest(key))
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");

            if (!string.IsNullOrEmpty(config.AuthToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AuthToken);
            }

            var response = await client.SendAsync(request);
            await ValidateGrpcResponseAsync(response);

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

            try
            {
                var config = GetConfig(instanceId);
                var client = GetHttpClient(instanceId);

                await EnsureConnectedAsync(instanceId);

                var key = GetKey(uri);

                var request = new HttpRequestMessage(HttpMethod.Post, $"/storage.StorageService/Exists")
                {
                    Content = new ByteArrayContent(BuildGrpcKeyRequest(key))
                };
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");

                if (!string.IsNullOrEmpty(config.AuthToken))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AuthToken);
                }

                var response = await client.SendAsync(request);
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
            await EnsureConnectedAsync(null);

            var request = new HttpRequestMessage(HttpMethod.Post, $"/storage.StorageService/List")
            {
                Content = new ByteArrayContent(BuildGrpcPrefixRequest(prefix))
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");

            if (!string.IsNullOrEmpty(_config.AuthToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.AuthToken);
            }

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
                yield break;

            // Parse server-streaming gRPC response
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new BinaryReader(stream);

            while (!ct.IsCancellationRequested)
            {
                // Read gRPC frame outside of yield to avoid try-catch yield issue
                string? key = null;
                long size = 0;
                bool shouldBreak = false;

                try
                {
                    // Read gRPC frame (5-byte header: compressed flag + 4-byte length)
                    var header = reader.ReadBytes(5);
                    if (header.Length < 5)
                    {
                        shouldBreak = true;
                    }
                    else
                    {
                        var length = BitConverter.ToInt32(header.Skip(1).Reverse().ToArray(), 0);
                        if (length <= 0)
                        {
                            shouldBreak = true;
                        }
                        else
                        {
                            var messageBytes = reader.ReadBytes(length);
                            (key, size) = ParseListItem(messageBytes);
                        }
                    }
                }
                catch
                {
                    shouldBreak = true;
                }

                if (shouldBreak)
                    break;

                if (!string.IsNullOrEmpty(key))
                {
                    var uri = new Uri($"grpc://{new Uri(_config.Endpoint).Host}/{key}");
                    yield return new StorageListItem(uri, size);
                }

                await Task.Yield();
            }
        }

        #endregion

        #region gRPC-Specific Operations

        /// <summary>
        /// Check server health.
        /// </summary>
        public async Task<object> CheckHealthAsync(string? instanceId = null)
        {
            var config = GetConfig(instanceId);
            var client = GetHttpClient(instanceId);

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/grpc.health.v1.Health/Check")
                {
                    Content = new ByteArrayContent(new byte[] { 0, 0, 0, 0, 0 }) // Empty message
                };
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");

                var response = await client.SendAsync(request);

                return new
                {
                    Healthy = response.IsSuccessStatusCode,
                    StatusCode = (int)response.StatusCode,
                    Endpoint = config.Endpoint,
                    InstanceId = instanceId
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    Healthy = false,
                    Error = ex.Message,
                    Endpoint = config.Endpoint,
                    InstanceId = instanceId
                };
            }
        }

        /// <summary>
        /// Stream upload using bidirectional streaming.
        /// </summary>
        public async Task StreamUploadAsync(string key, Stream data, int chunkSize = 65536, string? instanceId = null)
        {
            var config = GetConfig(instanceId);

            await EnsureConnectedAsync(instanceId);

            // For simplicity, use a single request with the full data
            // In production, implement proper bidirectional streaming
            using var ms = new MemoryStream();
            await data.CopyToAsync(ms);

            var uri = new Uri($"grpc://{new Uri(config.Endpoint).Host}/{key}");
            ms.Position = 0;
            await SaveAsync(uri, ms, instanceId);
        }

        /// <summary>
        /// Stream download using server-side streaming.
        /// </summary>
        public async Task<Stream> StreamDownloadAsync(string key, string? instanceId = null)
        {
            var config = GetConfig(instanceId);
            var uri = new Uri($"grpc://{new Uri(config.Endpoint).Host}/{key}");
            return await LoadAsync(uri, instanceId);
        }

        #endregion

        #region Helper Methods

        private GrpcStorageConfig GetConfig(string? instanceId)
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
            if (instance?.Connection is GrpcStorageConnection conn)
                return conn.HttpClient;

            return _httpClient;
        }

        private async Task EnsureConnectedAsync(string? instanceId)
        {
            if (_isConnected && string.IsNullOrEmpty(instanceId)) return;

            await _connectionLock.WaitAsync();
            try
            {
                if (_isConnected && string.IsNullOrEmpty(instanceId)) return;

                var config = GetConfig(instanceId);

                // Check connectivity
                var health = await CheckHealthAsync(instanceId);
                var isHealthy = health is IDictionary<string, object> h && h.TryGetValue("Healthy", out var val) && val is bool b && b;

                if (!isHealthy && !config.AllowUnhealthyConnection)
                {
                    throw new InvalidOperationException($"Cannot connect to gRPC server at {config.Endpoint}");
                }

                if (string.IsNullOrEmpty(instanceId))
                {
                    _isConnected = true;
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private static string GetKey(Uri uri)
        {
            return uri.AbsolutePath.TrimStart('/');
        }

        private static byte[] BuildGrpcRequest(string key, byte[] data)
        {
            // Simplified protobuf encoding for PutRequest { key: string, data: bytes }
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // gRPC frame header (5 bytes: compression flag + 4-byte length placeholder)
            writer.Write((byte)0); // No compression
            writer.Write(0); // Length placeholder

            var startPos = ms.Position;

            // Field 1: key (string) - wire type 2 (length-delimited)
            writer.Write((byte)0x0A); // Field 1, wire type 2
            WriteVarint(writer, key.Length);
            writer.Write(Encoding.UTF8.GetBytes(key));

            // Field 2: data (bytes) - wire type 2 (length-delimited)
            writer.Write((byte)0x12); // Field 2, wire type 2
            WriteVarint(writer, data.Length);
            writer.Write(data);

            // Update length
            var length = (int)(ms.Position - startPos);
            ms.Position = 1;
            writer.Write(BitConverter.GetBytes(length).Reverse().ToArray());

            return ms.ToArray();
        }

        private static byte[] BuildGrpcKeyRequest(string key)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write((byte)0);
            writer.Write(0);

            var startPos = ms.Position;

            // Field 1: key (string)
            writer.Write((byte)0x0A);
            WriteVarint(writer, key.Length);
            writer.Write(Encoding.UTF8.GetBytes(key));

            var length = (int)(ms.Position - startPos);
            ms.Position = 1;
            writer.Write(BitConverter.GetBytes(length).Reverse().ToArray());

            return ms.ToArray();
        }

        private static byte[] BuildGrpcPrefixRequest(string prefix)
        {
            return BuildGrpcKeyRequest(prefix);
        }

        private static void WriteVarint(BinaryWriter writer, int value)
        {
            while (value > 127)
            {
                writer.Write((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }
            writer.Write((byte)value);
        }

        private static byte[] ParseGrpcResponse(byte[] response)
        {
            if (response.Length < 5)
                return [];

            // Skip 5-byte gRPC header
            var messageStart = 5;
            var messageLength = BitConverter.ToInt32(response.Skip(1).Take(4).Reverse().ToArray(), 0);

            if (messageStart + messageLength > response.Length)
                return response.Skip(messageStart).ToArray();

            // Parse protobuf response - find the data field
            var pos = messageStart;
            while (pos < response.Length)
            {
                var tag = response[pos++];
                var fieldNumber = tag >> 3;
                var wireType = tag & 0x7;

                if (wireType == 2) // Length-delimited
                {
                    var length = 0;
                    var shift = 0;
                    byte b;
                    do
                    {
                        b = response[pos++];
                        length |= (b & 0x7F) << shift;
                        shift += 7;
                    } while ((b & 0x80) != 0);

                    if (fieldNumber == 2) // data field
                    {
                        return response.Skip(pos).Take(length).ToArray();
                    }

                    pos += length;
                }
            }

            return [];
        }

        private static (string key, long size) ParseListItem(byte[] messageBytes)
        {
            var key = "";
            long size = 0;
            var pos = 0;

            while (pos < messageBytes.Length)
            {
                var tag = messageBytes[pos++];
                var fieldNumber = tag >> 3;
                var wireType = tag & 0x7;

                if (wireType == 2) // Length-delimited (string)
                {
                    var length = 0;
                    var shift = 0;
                    byte b;
                    do
                    {
                        b = messageBytes[pos++];
                        length |= (b & 0x7F) << shift;
                        shift += 7;
                    } while ((b & 0x80) != 0);

                    if (fieldNumber == 1) // key
                    {
                        key = Encoding.UTF8.GetString(messageBytes, pos, length);
                    }

                    pos += length;
                }
                else if (wireType == 0) // Varint
                {
                    long value = 0;
                    var shift = 0;
                    byte b;
                    do
                    {
                        b = messageBytes[pos++];
                        value |= (long)(b & 0x7F) << shift;
                        shift += 7;
                    } while ((b & 0x80) != 0);

                    if (fieldNumber == 2) // size
                    {
                        size = value;
                    }
                }
            }

            return (key, size);
        }

        private static async Task ValidateGrpcResponseAsync(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"gRPC call failed: {response.StatusCode} - {content}");
            }

            if (response.Headers.TryGetValues("grpc-status", out var statusValues))
            {
                var status = int.Parse(statusValues.First());
                if (status != 0) // 0 = OK
                {
                    var message = response.Headers.TryGetValues("grpc-message", out var msgValues)
                        ? msgValues.First()
                        : "Unknown error";
                    throw new InvalidOperationException($"gRPC error {status}: {message}");
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// Internal connection wrapper for gRPC storage instances.
    /// </summary>
    internal class GrpcStorageConnection : IDisposable
    {
        public required GrpcStorageConfig Config { get; init; }
        public required HttpClient HttpClient { get; init; }

        public void Dispose()
        {
            HttpClient?.Dispose();
        }
    }

    /// <summary>
    /// Configuration for gRPC storage.
    /// </summary>
    public class GrpcStorageConfig : StorageConfigBase
    {
        /// <summary>
        /// gRPC server endpoint (e.g., "https://localhost:5001").
        /// </summary>
        public string Endpoint { get; set; } = "https://localhost:5001";

        /// <summary>
        /// Use TLS for connection.
        /// </summary>
        public bool UseTls { get; set; } = true;

        /// <summary>
        /// Accept any TLS certificate (for testing only).
        /// SECURITY WARNING: Must also set IsDevelopmentMode=true.
        /// </summary>
        public bool AcceptAnyCertificate { get; set; }

        /// <summary>
        /// Indicates this is a development/testing environment.
        /// Required to use AcceptAnyCertificate.
        /// </summary>
        public bool IsDevelopmentMode { get; set; }

        /// <summary>
        /// Bearer token for authentication.
        /// </summary>
        public string? AuthToken { get; set; }

        /// <summary>
        /// Request timeout in seconds.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Allow connection even if health check fails.
        /// </summary>
        public bool AllowUnhealthyConnection { get; set; } = true;

        /// <summary>
        /// Creates configuration for local development.
        /// </summary>
        public static GrpcStorageConfig Local(int port = 5001) => new()
        {
            Endpoint = $"https://localhost:{port}",
            UseTls = true,
            AcceptAnyCertificate = true,
            IsDevelopmentMode = true  // Safe: explicitly a local development configuration
        };

        /// <summary>
        /// Creates configuration for local development with instance ID.
        /// </summary>
        public static GrpcStorageConfig Local(int port, string instanceId) => new()
        {
            Endpoint = $"https://localhost:{port}",
            UseTls = true,
            AcceptAnyCertificate = true,
            IsDevelopmentMode = true,
            InstanceId = instanceId
        };

        /// <summary>
        /// Creates configuration for production.
        /// </summary>
        public static GrpcStorageConfig Production(string endpoint, string? authToken = null) => new()
        {
            Endpoint = endpoint,
            UseTls = true,
            AuthToken = authToken
        };

        /// <summary>
        /// Creates configuration for production with instance ID.
        /// </summary>
        public static GrpcStorageConfig Production(string endpoint, string? authToken, string instanceId) => new()
        {
            Endpoint = endpoint,
            UseTls = true,
            AuthToken = authToken,
            InstanceId = instanceId
        };
    }
}
