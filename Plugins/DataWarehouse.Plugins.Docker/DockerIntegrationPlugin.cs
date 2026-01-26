using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.Docker
{
    /// <summary>
    /// First-class Docker integration plugin providing comprehensive Docker support.
    /// Implements Docker Volume Driver, Log Driver, and Secret Backend specifications.
    ///
    /// Features:
    /// - Docker Volume Plugin API v1.0 (Create, Remove, Mount, Unmount, Get, List, Path, Capabilities)
    /// - Docker Log Driver for shipping container logs to DataWarehouse
    /// - Docker Secret Backend for storing secrets in DataWarehouse
    /// - Support for multiple storage backends (S3, local, etc.)
    /// - Built-in encryption and storage tiering
    /// - Health checks and graceful shutdown
    /// - Rootless mode support
    /// - cgroup limit awareness
    ///
    /// Message Commands:
    /// - docker.volume.*: Volume driver operations
    /// - docker.log.*: Log driver operations
    /// - docker.secret.*: Secret backend operations
    /// - docker.health: Health check
    /// </summary>
    public sealed class DockerIntegrationPlugin : FeaturePluginBase
    {
        private readonly DockerPluginConfig _config;
        private readonly DockerVolumeDriver _volumeDriver;
        private readonly DockerLogDriver _logDriver;
        private readonly DockerSecretBackend _secretBackend;
        private readonly CancellationTokenSource _shutdownCts = new();
        private TcpListener? _listener;
        private Task? _serverTask;
        private readonly SemaphoreSlim _shutdownLock = new(1, 1);
        private volatile bool _isRunning;

        /// <summary>
        /// Unique plugin identifier following reverse domain notation.
        /// </summary>
        public override string Id => "com.datawarehouse.plugins.docker";

        /// <summary>
        /// Human-readable plugin name.
        /// </summary>
        public override string Name => "Docker Integration";

        /// <summary>
        /// Plugin version following semantic versioning.
        /// </summary>
        public override string Version => "1.0.0";

        /// <summary>
        /// Plugin category for classification.
        /// </summary>
        public override PluginCategory Category => PluginCategory.FeatureProvider;

        /// <summary>
        /// Initializes the Docker integration plugin with optional configuration.
        /// </summary>
        /// <param name="config">Configuration options for Docker integration.</param>
        /// <exception cref="ArgumentNullException">Thrown when required configuration is null.</exception>
        public DockerIntegrationPlugin(DockerPluginConfig? config = null)
        {
            _config = config ?? new DockerPluginConfig();
            _volumeDriver = new DockerVolumeDriver(_config);
            _logDriver = new DockerLogDriver(_config);
            _secretBackend = new DockerSecretBackend(_config);
        }

        /// <summary>
        /// Starts the Docker plugin server and initializes all components.
        /// </summary>
        /// <param name="ct">Cancellation token for graceful shutdown.</param>
        public override async Task StartAsync(CancellationToken ct)
        {
            await _shutdownLock.WaitAsync(ct);
            try
            {
                if (_isRunning)
                {
                    return;
                }

                // Initialize storage directories
                EnsureDirectoriesExist();

                // Start the plugin HTTP server for Docker daemon communication
                _listener = new TcpListener(
                    _config.ListenAddress ?? IPAddress.Loopback,
                    _config.ListenPort);
                _listener.Start();

                _isRunning = true;
                _serverTask = AcceptConnectionsAsync(_shutdownCts.Token);

                await _volumeDriver.InitializeAsync(ct);
                await _logDriver.InitializeAsync(ct);
                await _secretBackend.InitializeAsync(ct);
            }
            finally
            {
                _shutdownLock.Release();
            }
        }

        /// <summary>
        /// Stops the Docker plugin server with graceful shutdown.
        /// </summary>
        public override async Task StopAsync()
        {
            await _shutdownLock.WaitAsync();
            try
            {
                if (!_isRunning)
                {
                    return;
                }

                _isRunning = false;
                _shutdownCts.Cancel();
                _listener?.Stop();

                if (_serverTask != null)
                {
                    try
                    {
                        await _serverTask.WaitAsync(TimeSpan.FromSeconds(_config.ShutdownTimeoutSeconds));
                    }
                    catch (TimeoutException)
                    {
                        // Forceful shutdown after timeout
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected during shutdown
                    }
                }

                await _volumeDriver.ShutdownAsync();
                await _logDriver.ShutdownAsync();
                await _secretBackend.ShutdownAsync();
            }
            finally
            {
                _shutdownLock.Release();
            }
        }

        /// <summary>
        /// Handles incoming plugin messages via the message bus.
        /// </summary>
        /// <param name="message">The plugin message to process.</param>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            var response = message.Type switch
            {
                // Volume driver messages
                "docker.volume.create" => await HandleVolumeCreateAsync(message),
                "docker.volume.remove" => await HandleVolumeRemoveAsync(message),
                "docker.volume.mount" => await HandleVolumeMountAsync(message),
                "docker.volume.unmount" => await HandleVolumeUnmountAsync(message),
                "docker.volume.get" => await HandleVolumeGetAsync(message),
                "docker.volume.list" => await HandleVolumeListAsync(message),
                "docker.volume.path" => await HandleVolumePathAsync(message),
                "docker.volume.capabilities" => HandleVolumeCapabilities(),

                // Log driver messages
                "docker.log.start" => await HandleLogStartAsync(message),
                "docker.log.stop" => await HandleLogStopAsync(message),
                "docker.log.read" => await HandleLogReadAsync(message),
                "docker.log.capabilities" => HandleLogCapabilities(),

                // Secret backend messages
                "docker.secret.store" => await HandleSecretStoreAsync(message),
                "docker.secret.get" => await HandleSecretGetAsync(message),
                "docker.secret.remove" => await HandleSecretRemoveAsync(message),
                "docker.secret.list" => await HandleSecretListAsync(message),

                // Health check
                "docker.health" => HandleHealthCheck(),

                _ => MessageResponse.Error($"Unknown message type: {message.Type}")
            };

            // Response is returned via message correlation
        }

        /// <summary>
        /// Gets plugin capabilities for AI agents.
        /// </summary>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "docker.volume.create", DisplayName = "Create Volume", Description = "Create a new Docker volume" },
                new() { Name = "docker.volume.remove", DisplayName = "Remove Volume", Description = "Remove a Docker volume" },
                new() { Name = "docker.volume.mount", DisplayName = "Mount Volume", Description = "Mount a Docker volume" },
                new() { Name = "docker.volume.unmount", DisplayName = "Unmount Volume", Description = "Unmount a Docker volume" },
                new() { Name = "docker.volume.get", DisplayName = "Get Volume", Description = "Get volume information" },
                new() { Name = "docker.volume.list", DisplayName = "List Volumes", Description = "List all volumes" },
                new() { Name = "docker.log.start", DisplayName = "Start Logging", Description = "Start logging for a container" },
                new() { Name = "docker.log.stop", DisplayName = "Stop Logging", Description = "Stop logging for a container" },
                new() { Name = "docker.log.read", DisplayName = "Read Logs", Description = "Read container logs" },
                new() { Name = "docker.secret.store", DisplayName = "Store Secret", Description = "Store a Docker secret" },
                new() { Name = "docker.secret.get", DisplayName = "Get Secret", Description = "Retrieve a Docker secret" },
                new() { Name = "docker.secret.remove", DisplayName = "Remove Secret", Description = "Remove a Docker secret" },
                new() { Name = "docker.secret.list", DisplayName = "List Secrets", Description = "List all secrets" },
                new() { Name = "docker.health", DisplayName = "Health Check", Description = "Check plugin health" }
            };
        }

        /// <summary>
        /// Gets plugin metadata for discovery and AI integration.
        /// </summary>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Description"] = "First-class Docker integration with volume driver, log driver, and secret backend";
            metadata["FeatureType"] = "DockerIntegration";
            metadata["SupportsVolumeDriver"] = true;
            metadata["SupportsLogDriver"] = true;
            metadata["SupportsSecretBackend"] = true;
            metadata["SupportsEncryption"] = true;
            metadata["SupportsTiering"] = true;
            metadata["SupportsRootless"] = true;
            metadata["ListenPort"] = _config.ListenPort;
            metadata["StorageBackend"] = _config.StorageBackend.ToString();
            return metadata;
        }

        #region Volume Driver Handlers

        private async Task<MessageResponse> HandleVolumeCreateAsync(PluginMessage message)
        {
            var payload = ExtractPayload(message);
            var volumeName = GetRequiredString(payload, "name");
            var options = GetOptionalDictionary(payload, "options");

            var result = await _volumeDriver.CreateVolumeAsync(volumeName, options);
            return MessageResponse.Ok(result);
        }

        private async Task<MessageResponse> HandleVolumeRemoveAsync(PluginMessage message)
        {
            var payload = ExtractPayload(message);
            var volumeName = GetRequiredString(payload, "name");

            await _volumeDriver.RemoveVolumeAsync(volumeName);
            return MessageResponse.Ok(new { Success = true });
        }

        private async Task<MessageResponse> HandleVolumeMountAsync(PluginMessage message)
        {
            var payload = ExtractPayload(message);
            var volumeName = GetRequiredString(payload, "name");
            var containerId = GetRequiredString(payload, "containerId");

            var mountpoint = await _volumeDriver.MountVolumeAsync(volumeName, containerId);
            return MessageResponse.Ok(new { Mountpoint = mountpoint });
        }

        private async Task<MessageResponse> HandleVolumeUnmountAsync(PluginMessage message)
        {
            var payload = ExtractPayload(message);
            var volumeName = GetRequiredString(payload, "name");
            var containerId = GetRequiredString(payload, "containerId");

            await _volumeDriver.UnmountVolumeAsync(volumeName, containerId);
            return MessageResponse.Ok(new { Success = true });
        }

        private async Task<MessageResponse> HandleVolumeGetAsync(PluginMessage message)
        {
            var payload = ExtractPayload(message);
            var volumeName = GetRequiredString(payload, "name");

            var volume = await _volumeDriver.GetVolumeAsync(volumeName);
            return volume != null
                ? MessageResponse.Ok(volume)
                : MessageResponse.Error($"Volume not found: {volumeName}");
        }

        private async Task<MessageResponse> HandleVolumeListAsync(PluginMessage message)
        {
            var volumes = await _volumeDriver.ListVolumesAsync();
            return MessageResponse.Ok(new { Volumes = volumes });
        }

        private async Task<MessageResponse> HandleVolumePathAsync(PluginMessage message)
        {
            var payload = ExtractPayload(message);
            var volumeName = GetRequiredString(payload, "name");

            var path = await _volumeDriver.GetVolumePathAsync(volumeName);
            return MessageResponse.Ok(new { Mountpoint = path });
        }

        private MessageResponse HandleVolumeCapabilities()
        {
            var capabilities = _volumeDriver.GetCapabilities();
            return MessageResponse.Ok(capabilities);
        }

        #endregion

        #region Log Driver Handlers

        private async Task<MessageResponse> HandleLogStartAsync(PluginMessage message)
        {
            var payload = ExtractPayload(message);
            var containerId = GetRequiredString(payload, "containerId");
            var containerName = GetOptionalString(payload, "containerName");
            var config = GetOptionalDictionary(payload, "config");

            await _logDriver.StartLoggingAsync(containerId, containerName, config);
            return MessageResponse.Ok(new { Success = true });
        }

        private async Task<MessageResponse> HandleLogStopAsync(PluginMessage message)
        {
            var payload = ExtractPayload(message);
            var containerId = GetRequiredString(payload, "containerId");

            await _logDriver.StopLoggingAsync(containerId);
            return MessageResponse.Ok(new { Success = true });
        }

        private async Task<MessageResponse> HandleLogReadAsync(PluginMessage message)
        {
            var payload = ExtractPayload(message);
            var containerId = GetRequiredString(payload, "containerId");
            var config = GetOptionalDictionary(payload, "config");

            var logs = await _logDriver.ReadLogsAsync(containerId, config);
            return MessageResponse.Ok(new { Logs = logs });
        }

        private MessageResponse HandleLogCapabilities()
        {
            var capabilities = _logDriver.GetCapabilities();
            return MessageResponse.Ok(capabilities);
        }

        #endregion

        #region Secret Backend Handlers

        private async Task<MessageResponse> HandleSecretStoreAsync(PluginMessage message)
        {
            var payload = ExtractPayload(message);
            var secretId = GetRequiredString(payload, "secretId");
            var secretData = GetRequiredBytes(payload, "data");
            var labels = GetOptionalDictionary(payload, "labels");

            await _secretBackend.StoreSecretAsync(secretId, secretData, labels);
            return MessageResponse.Ok(new { Success = true });
        }

        private async Task<MessageResponse> HandleSecretGetAsync(PluginMessage message)
        {
            var payload = ExtractPayload(message);
            var secretId = GetRequiredString(payload, "secretId");

            var secretData = await _secretBackend.GetSecretAsync(secretId);
            return secretData != null
                ? MessageResponse.Ok(new { SecretId = secretId, Data = Convert.ToBase64String(secretData) })
                : MessageResponse.Error($"Secret not found: {secretId}");
        }

        private async Task<MessageResponse> HandleSecretRemoveAsync(PluginMessage message)
        {
            var payload = ExtractPayload(message);
            var secretId = GetRequiredString(payload, "secretId");

            await _secretBackend.RemoveSecretAsync(secretId);
            return MessageResponse.Ok(new { Success = true });
        }

        private async Task<MessageResponse> HandleSecretListAsync(PluginMessage message)
        {
            var secrets = await _secretBackend.ListSecretsAsync();
            return MessageResponse.Ok(new { Secrets = secrets });
        }

        #endregion

        #region Health Check

        private MessageResponse HandleHealthCheck()
        {
            var health = new DockerHealthStatus
            {
                IsHealthy = _isRunning,
                VolumeDriverStatus = _volumeDriver.GetHealthStatus(),
                LogDriverStatus = _logDriver.GetHealthStatus(),
                SecretBackendStatus = _secretBackend.GetHealthStatus(),
                MemoryUsageBytes = GetCurrentMemoryUsage(),
                CgroupLimitsRespected = CheckCgroupLimits()
            };

            return MessageResponse.Ok(health);
        }

        #endregion

        #region HTTP Server for Docker Plugin Protocol

        private async Task AcceptConnectionsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(ct);
                    _ = HandleClientAsync(client, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Log error and continue accepting connections
                    System.Diagnostics.Debug.WriteLine($"Error accepting connection: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                try
                {
                    var request = await ReadHttpRequestAsync(stream, ct);
                    var response = await ProcessDockerApiRequestAsync(request);
                    await WriteHttpResponseAsync(stream, response, ct);
                }
                catch (Exception ex)
                {
                    var errorResponse = new DockerApiResponse
                    {
                        StatusCode = 500,
                        Body = JsonSerializer.Serialize(new { Err = ex.Message })
                    };
                    await WriteHttpResponseAsync(stream, errorResponse, ct);
                }
            }
        }

        private async Task<DockerApiRequest> ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var bytesRead = await stream.ReadAsync(buffer, ct);
            var requestText = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            var lines = requestText.Split("\r\n");
            var requestLine = lines[0].Split(' ');
            var method = requestLine[0];
            var path = requestLine.Length > 1 ? requestLine[1] : "/";

            // Find body (after double CRLF)
            var bodyIndex = requestText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var body = bodyIndex >= 0 && bodyIndex + 4 < requestText.Length
                ? requestText[(bodyIndex + 4)..]
                : string.Empty;

            return new DockerApiRequest
            {
                Method = method,
                Path = path,
                Body = body
            };
        }

        private async Task<DockerApiResponse> ProcessDockerApiRequestAsync(DockerApiRequest request)
        {
            try
            {
                var (endpoint, body) = (request.Path, request.Body);

                // Docker Volume Plugin API endpoints
                return endpoint switch
                {
                    "/Plugin.Activate" => new DockerApiResponse
                    {
                        StatusCode = 200,
                        Body = JsonSerializer.Serialize(new { Implements = new[] { "VolumeDriver", "LogDriver" } })
                    },
                    "/VolumeDriver.Create" => await HandleVolumeCreateApiAsync(body),
                    "/VolumeDriver.Remove" => await HandleVolumeRemoveApiAsync(body),
                    "/VolumeDriver.Mount" => await HandleVolumeMountApiAsync(body),
                    "/VolumeDriver.Unmount" => await HandleVolumeUnmountApiAsync(body),
                    "/VolumeDriver.Get" => await HandleVolumeGetApiAsync(body),
                    "/VolumeDriver.List" => await HandleVolumeListApiAsync(),
                    "/VolumeDriver.Path" => await HandleVolumePathApiAsync(body),
                    "/VolumeDriver.Capabilities" => HandleVolumeCapabilitiesApi(),
                    "/LogDriver.StartLogging" => await HandleLogStartApiAsync(body),
                    "/LogDriver.StopLogging" => await HandleLogStopApiAsync(body),
                    "/LogDriver.ReadLogs" => await HandleLogReadApiAsync(body),
                    "/LogDriver.Capabilities" => HandleLogCapabilitiesApi(),
                    _ => new DockerApiResponse
                    {
                        StatusCode = 404,
                        Body = JsonSerializer.Serialize(new { Err = $"Unknown endpoint: {endpoint}" })
                    }
                };
            }
            catch (Exception ex)
            {
                return new DockerApiResponse
                {
                    StatusCode = 500,
                    Body = JsonSerializer.Serialize(new { Err = ex.Message })
                };
            }
        }

        private async Task WriteHttpResponseAsync(NetworkStream stream, DockerApiResponse response, CancellationToken ct)
        {
            var statusText = response.StatusCode == 200 ? "OK" : "Error";
            var responseText = $"HTTP/1.1 {response.StatusCode} {statusText}\r\n" +
                               "Content-Type: application/json\r\n" +
                               $"Content-Length: {Encoding.UTF8.GetByteCount(response.Body)}\r\n" +
                               "\r\n" +
                               response.Body;

            var responseBytes = Encoding.UTF8.GetBytes(responseText);
            await stream.WriteAsync(responseBytes, ct);
        }

        #endregion

        #region Docker API Handlers

        private async Task<DockerApiResponse> HandleVolumeCreateApiAsync(string body)
        {
            var request = JsonSerializer.Deserialize<VolumeCreateRequest>(body);
            if (request == null || string.IsNullOrEmpty(request.Name))
            {
                return new DockerApiResponse
                {
                    StatusCode = 400,
                    Body = JsonSerializer.Serialize(new { Err = "Invalid request: name is required" })
                };
            }

            var result = await _volumeDriver.CreateVolumeAsync(request.Name, request.Opts);
            return new DockerApiResponse
            {
                StatusCode = 200,
                Body = JsonSerializer.Serialize(new { Err = string.Empty })
            };
        }

        private async Task<DockerApiResponse> HandleVolumeRemoveApiAsync(string body)
        {
            var request = JsonSerializer.Deserialize<VolumeRemoveRequest>(body);
            if (request == null || string.IsNullOrEmpty(request.Name))
            {
                return new DockerApiResponse
                {
                    StatusCode = 400,
                    Body = JsonSerializer.Serialize(new { Err = "Invalid request: name is required" })
                };
            }

            await _volumeDriver.RemoveVolumeAsync(request.Name);
            return new DockerApiResponse
            {
                StatusCode = 200,
                Body = JsonSerializer.Serialize(new { Err = string.Empty })
            };
        }

        private async Task<DockerApiResponse> HandleVolumeMountApiAsync(string body)
        {
            var request = JsonSerializer.Deserialize<VolumeMountRequest>(body);
            if (request == null || string.IsNullOrEmpty(request.Name) || string.IsNullOrEmpty(request.ID))
            {
                return new DockerApiResponse
                {
                    StatusCode = 400,
                    Body = JsonSerializer.Serialize(new { Err = "Invalid request: name and ID are required" })
                };
            }

            var mountpoint = await _volumeDriver.MountVolumeAsync(request.Name, request.ID);
            return new DockerApiResponse
            {
                StatusCode = 200,
                Body = JsonSerializer.Serialize(new { Mountpoint = mountpoint, Err = string.Empty })
            };
        }

        private async Task<DockerApiResponse> HandleVolumeUnmountApiAsync(string body)
        {
            var request = JsonSerializer.Deserialize<VolumeUnmountRequest>(body);
            if (request == null || string.IsNullOrEmpty(request.Name) || string.IsNullOrEmpty(request.ID))
            {
                return new DockerApiResponse
                {
                    StatusCode = 400,
                    Body = JsonSerializer.Serialize(new { Err = "Invalid request: name and ID are required" })
                };
            }

            await _volumeDriver.UnmountVolumeAsync(request.Name, request.ID);
            return new DockerApiResponse
            {
                StatusCode = 200,
                Body = JsonSerializer.Serialize(new { Err = string.Empty })
            };
        }

        private async Task<DockerApiResponse> HandleVolumeGetApiAsync(string body)
        {
            var request = JsonSerializer.Deserialize<VolumeGetRequest>(body);
            if (request == null || string.IsNullOrEmpty(request.Name))
            {
                return new DockerApiResponse
                {
                    StatusCode = 400,
                    Body = JsonSerializer.Serialize(new { Err = "Invalid request: name is required" })
                };
            }

            var volume = await _volumeDriver.GetVolumeAsync(request.Name);
            if (volume == null)
            {
                return new DockerApiResponse
                {
                    StatusCode = 200,
                    Body = JsonSerializer.Serialize(new { Err = $"Volume not found: {request.Name}" })
                };
            }

            return new DockerApiResponse
            {
                StatusCode = 200,
                Body = JsonSerializer.Serialize(new
                {
                    Volume = new
                    {
                        volume.Name,
                        volume.Mountpoint,
                        volume.Status
                    },
                    Err = string.Empty
                })
            };
        }

        private async Task<DockerApiResponse> HandleVolumeListApiAsync()
        {
            var volumes = await _volumeDriver.ListVolumesAsync();
            var volumeList = volumes.Select(v => new
            {
                v.Name,
                v.Mountpoint
            }).ToArray();

            return new DockerApiResponse
            {
                StatusCode = 200,
                Body = JsonSerializer.Serialize(new { Volumes = volumeList, Err = string.Empty })
            };
        }

        private async Task<DockerApiResponse> HandleVolumePathApiAsync(string body)
        {
            var request = JsonSerializer.Deserialize<VolumePathRequest>(body);
            if (request == null || string.IsNullOrEmpty(request.Name))
            {
                return new DockerApiResponse
                {
                    StatusCode = 400,
                    Body = JsonSerializer.Serialize(new { Err = "Invalid request: name is required" })
                };
            }

            var path = await _volumeDriver.GetVolumePathAsync(request.Name);
            return new DockerApiResponse
            {
                StatusCode = 200,
                Body = JsonSerializer.Serialize(new { Mountpoint = path, Err = string.Empty })
            };
        }

        private DockerApiResponse HandleVolumeCapabilitiesApi()
        {
            var capabilities = _volumeDriver.GetCapabilities();
            return new DockerApiResponse
            {
                StatusCode = 200,
                Body = JsonSerializer.Serialize(new { Capabilities = capabilities })
            };
        }

        private async Task<DockerApiResponse> HandleLogStartApiAsync(string body)
        {
            var request = JsonSerializer.Deserialize<LogStartRequest>(body);
            if (request == null || string.IsNullOrEmpty(request.ContainerID))
            {
                return new DockerApiResponse
                {
                    StatusCode = 400,
                    Body = JsonSerializer.Serialize(new { Err = "Invalid request: ContainerID is required" })
                };
            }

            await _logDriver.StartLoggingAsync(request.ContainerID, request.ContainerName, request.Config);
            return new DockerApiResponse
            {
                StatusCode = 200,
                Body = JsonSerializer.Serialize(new { Err = string.Empty })
            };
        }

        private async Task<DockerApiResponse> HandleLogStopApiAsync(string body)
        {
            var request = JsonSerializer.Deserialize<LogStopRequest>(body);
            if (request == null || string.IsNullOrEmpty(request.ContainerID))
            {
                return new DockerApiResponse
                {
                    StatusCode = 400,
                    Body = JsonSerializer.Serialize(new { Err = "Invalid request: ContainerID is required" })
                };
            }

            await _logDriver.StopLoggingAsync(request.ContainerID);
            return new DockerApiResponse
            {
                StatusCode = 200,
                Body = JsonSerializer.Serialize(new { Err = string.Empty })
            };
        }

        private async Task<DockerApiResponse> HandleLogReadApiAsync(string body)
        {
            var request = JsonSerializer.Deserialize<LogReadRequest>(body);
            if (request == null || string.IsNullOrEmpty(request.ContainerID))
            {
                return new DockerApiResponse
                {
                    StatusCode = 400,
                    Body = JsonSerializer.Serialize(new { Err = "Invalid request: ContainerID is required" })
                };
            }

            var logs = await _logDriver.ReadLogsAsync(request.ContainerID, request.Config);
            return new DockerApiResponse
            {
                StatusCode = 200,
                Body = JsonSerializer.Serialize(new { Logs = logs, Err = string.Empty })
            };
        }

        private DockerApiResponse HandleLogCapabilitiesApi()
        {
            var capabilities = _logDriver.GetCapabilities();
            return new DockerApiResponse
            {
                StatusCode = 200,
                Body = JsonSerializer.Serialize(new { Capabilities = capabilities })
            };
        }

        #endregion

        #region Helper Methods

        private void EnsureDirectoriesExist()
        {
            Directory.CreateDirectory(_config.VolumeBasePath);
            Directory.CreateDirectory(_config.LogBasePath);
            Directory.CreateDirectory(_config.SecretBasePath);
        }

        private static Dictionary<string, object> ExtractPayload(PluginMessage message)
        {
            if (message.Payload is Dictionary<string, object> dict)
            {
                return dict;
            }
            throw new ArgumentException("Invalid message payload");
        }

        private static string GetRequiredString(Dictionary<string, object> payload, string key)
        {
            if (payload.TryGetValue(key, out var value) && value is string str && !string.IsNullOrEmpty(str))
            {
                return str;
            }
            throw new ArgumentException($"Required parameter missing: {key}");
        }

        private static string? GetOptionalString(Dictionary<string, object> payload, string key)
        {
            if (payload.TryGetValue(key, out var value) && value is string str)
            {
                return str;
            }
            return null;
        }

        private static Dictionary<string, string>? GetOptionalDictionary(Dictionary<string, object> payload, string key)
        {
            if (payload.TryGetValue(key, out var value))
            {
                if (value is Dictionary<string, string> dict)
                {
                    return dict;
                }
                if (value is Dictionary<string, object> objDict)
                {
                    return objDict.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString() ?? string.Empty);
                }
            }
            return null;
        }

        private static byte[] GetRequiredBytes(Dictionary<string, object> payload, string key)
        {
            if (payload.TryGetValue(key, out var value))
            {
                if (value is byte[] bytes)
                {
                    return bytes;
                }
                if (value is string base64)
                {
                    return Convert.FromBase64String(base64);
                }
            }
            throw new ArgumentException($"Required parameter missing: {key}");
        }

        private static long GetCurrentMemoryUsage()
        {
            return GC.GetTotalMemory(false);
        }

        private bool CheckCgroupLimits()
        {
            try
            {
                // Check if running in a container with cgroup limits
                var cgroupMemLimit = "/sys/fs/cgroup/memory/memory.limit_in_bytes";
                if (File.Exists(cgroupMemLimit))
                {
                    var limitStr = File.ReadAllText(cgroupMemLimit).Trim();
                    if (long.TryParse(limitStr, out var limit) && limit < long.MaxValue)
                    {
                        var currentUsage = GetCurrentMemoryUsage();
                        return currentUsage < limit * 0.9; // Under 90% of limit
                    }
                }

                // cgroup v2
                var cgroupV2MemMax = "/sys/fs/cgroup/memory.max";
                if (File.Exists(cgroupV2MemMax))
                {
                    var content = File.ReadAllText(cgroupV2MemMax).Trim();
                    if (content != "max" && long.TryParse(content, out var limit))
                    {
                        var currentUsage = GetCurrentMemoryUsage();
                        return currentUsage < limit * 0.9;
                    }
                }

                return true; // No cgroup limits or not in container
            }
            catch
            {
                return true;
            }
        }

        #endregion
    }

    #region Docker API Request/Response Models

    internal class DockerApiRequest
    {
        public string Method { get; init; } = "POST";
        public string Path { get; init; } = "/";
        public string Body { get; init; } = string.Empty;
    }

    internal class DockerApiResponse
    {
        public int StatusCode { get; init; }
        public string Body { get; init; } = string.Empty;
    }

    internal class VolumeCreateRequest
    {
        [JsonPropertyName("Name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("Opts")]
        public Dictionary<string, string>? Opts { get; init; }
    }

    internal class VolumeRemoveRequest
    {
        [JsonPropertyName("Name")]
        public string Name { get; init; } = string.Empty;
    }

    internal class VolumeMountRequest
    {
        [JsonPropertyName("Name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("ID")]
        public string ID { get; init; } = string.Empty;
    }

    internal class VolumeUnmountRequest
    {
        [JsonPropertyName("Name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("ID")]
        public string ID { get; init; } = string.Empty;
    }

    internal class VolumeGetRequest
    {
        [JsonPropertyName("Name")]
        public string Name { get; init; } = string.Empty;
    }

    internal class VolumePathRequest
    {
        [JsonPropertyName("Name")]
        public string Name { get; init; } = string.Empty;
    }

    internal class LogStartRequest
    {
        [JsonPropertyName("ContainerID")]
        public string ContainerID { get; init; } = string.Empty;

        [JsonPropertyName("ContainerName")]
        public string? ContainerName { get; init; }

        [JsonPropertyName("Config")]
        public Dictionary<string, string>? Config { get; init; }
    }

    internal class LogStopRequest
    {
        [JsonPropertyName("ContainerID")]
        public string ContainerID { get; init; } = string.Empty;
    }

    internal class LogReadRequest
    {
        [JsonPropertyName("ContainerID")]
        public string ContainerID { get; init; } = string.Empty;

        [JsonPropertyName("Config")]
        public Dictionary<string, string>? Config { get; init; }
    }

    #endregion
}
