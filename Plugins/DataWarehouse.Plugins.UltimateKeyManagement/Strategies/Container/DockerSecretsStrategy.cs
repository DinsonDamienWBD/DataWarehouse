using DataWarehouse.SDK.Security;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Container
{
    /// <summary>
    /// Docker Swarm Secrets-based KeyStore strategy for containerized deployments.
    /// Implements IKeyStoreStrategy with Docker Swarm secrets integration.
    ///
    /// Features:
    /// - Read secrets from /run/secrets mount (Docker Swarm standard)
    /// - Create/update secrets via Docker Engine API (Unix socket or TCP)
    /// - Support for secret labels and metadata
    /// - Automatic secret rotation with versioning
    /// - Health checks via Docker Engine API
    ///
    /// Configuration:
    /// - SecretsPath: Base path for mounted secrets (default: "/run/secrets")
    /// - SecretNamePrefix: Prefix for secret names (default: "datawarehouse_key_")
    /// - DockerHost: Docker Engine API endpoint (default: "unix:///var/run/docker.sock")
    /// - LabelPrefix: Label prefix for secrets (default: "com.datawarehouse.")
    /// - AllowApiWrites: Allow creating/updating secrets via API (default: true)
    ///
    /// Note: Secret creation requires Docker Swarm mode and appropriate permissions.
    /// Reading secrets works in both standalone and Swarm mode when mounted via docker-compose or stack.
    /// </summary>
    public sealed class DockerSecretsStrategy : KeyStoreStrategyBase
    {
        private DockerSecretsConfig _config = new();
        private HttpClient? _dockerClient;
        private string? _currentKeyId;
        private bool _isSwarmMode;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = false,
            SupportsReplication = true,  // Swarm replicates secrets to nodes
            SupportsVersioning = true,   // Via secret versioning
            SupportsPerKeyAcl = false,   // Docker doesn't support per-secret ACL
            SupportsAuditLogging = false,
            MaxKeySizeBytes = 500 * 1024, // Docker limits secrets to 500KB
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["StorageType"] = "DockerSecrets",
                ["Platform"] = "Docker/Swarm",
                ["ReadMethod"] = "FilesystemMount",
                ["WriteMethod"] = "DockerEngineAPI",
                ["Encryption"] = "Raft log encryption (Swarm)"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("dockersecrets.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("dockersecrets.init");
            LoadConfiguration();

            // Initialize Docker API client
            _dockerClient = CreateDockerClient();

            // Check if Docker is running and if we're in Swarm mode
            await DetectSwarmModeAsync(cancellationToken);

            // Ensure secrets directory exists (for reading)
            if (!Directory.Exists(_config.SecretsPath))
            {
                // Not a fatal error - may be running outside Docker
                System.Diagnostics.Trace.TraceWarning(
                    $"Docker secrets path '{_config.SecretsPath}' does not exist. " +
                    "Secrets will only be available via Docker API.");
            }

            // Initialize current key ID
            await InitializeCurrentKeyIdAsync(cancellationToken);
        }

        private void LoadConfiguration()
        {
            if (Configuration.TryGetValue("SecretsPath", out var pathObj) && pathObj is string path)
                _config.SecretsPath = path;
            if (Configuration.TryGetValue("SecretNamePrefix", out var prefixObj) && prefixObj is string prefix)
                _config.SecretNamePrefix = prefix;
            if (Configuration.TryGetValue("DockerHost", out var hostObj) && hostObj is string host)
                _config.DockerHost = host;
            if (Configuration.TryGetValue("LabelPrefix", out var labelObj) && labelObj is string label)
                _config.LabelPrefix = label;
            if (Configuration.TryGetValue("AllowApiWrites", out var allowWritesObj) && allowWritesObj is bool allowWrites)
                _config.AllowApiWrites = allowWrites;
            if (Configuration.TryGetValue("TlsCertPath", out var certObj) && certObj is string cert)
                _config.TlsCertPath = cert;
            if (Configuration.TryGetValue("TlsKeyPath", out var keyObj) && keyObj is string key)
                _config.TlsKeyPath = key;
            if (Configuration.TryGetValue("TlsCaPath", out var caObj) && caObj is string ca)
                _config.TlsCaPath = ca;
        }

        private HttpClient CreateDockerClient()
        {
            HttpMessageHandler handler;

            if (_config.DockerHost.StartsWith("unix://"))
            {
                // Unix socket connection
                var socketPath = _config.DockerHost.Substring(7);
                handler = new SocketsHttpHandler
                {
                    ConnectCallback = async (context, cancellationToken) =>
                    {
                        var socket = new System.Net.Sockets.Socket(
                            System.Net.Sockets.AddressFamily.Unix,
                            System.Net.Sockets.SocketType.Stream,
                            System.Net.Sockets.ProtocolType.Unspecified);

                        var endpoint = new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath);
                        await socket.ConnectAsync(endpoint, cancellationToken);

                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    }
                };
            }
            else if (_config.DockerHost.StartsWith("npipe://"))
            {
                // Windows named pipe connection
                var pipeName = _config.DockerHost.Substring(8).Replace("/", "\\");
                handler = new SocketsHttpHandler
                {
                    ConnectCallback = async (context, cancellationToken) =>
                    {
                        var pipe = new System.IO.Pipes.NamedPipeClientStream(
                            ".",
                            pipeName,
                            System.IO.Pipes.PipeDirection.InOut,
                            System.IO.Pipes.PipeOptions.Asynchronous);

                        await pipe.ConnectAsync(cancellationToken);
                        return pipe;
                    }
                };
            }
            else
            {
                // TCP connection (with optional TLS)
                var httpHandler = new HttpClientHandler();

                if (!string.IsNullOrEmpty(_config.TlsCertPath))
                {
                    var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(
                        _config.TlsCertPath);
                    httpHandler.ClientCertificates.Add(cert);
                }

                handler = httpHandler;
            }

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(_config.DockerHost.StartsWith("unix://") || _config.DockerHost.StartsWith("npipe://")
                    ? "http://localhost"
                    : _config.DockerHost),
                Timeout = TimeSpan.FromSeconds(30)
            };

            return client;
        }

        private async Task DetectSwarmModeAsync(CancellationToken cancellationToken)
        {
            try
            {
                var response = await _dockerClient!.GetAsync("/info", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var info = await response.Content.ReadFromJsonAsync<DockerInfo>(cancellationToken: cancellationToken);
                    _isSwarmMode = info?.Swarm?.LocalNodeState == "active";
                }
            }
            catch
            {
                _isSwarmMode = false;
            }
        }

        private async Task InitializeCurrentKeyIdAsync(CancellationToken cancellationToken)
        {
            // First, try to find keys from filesystem
            if (Directory.Exists(_config.SecretsPath))
            {
                var secretFiles = Directory.GetFiles(_config.SecretsPath)
                    .Where(f => Path.GetFileName(f).StartsWith(_config.SecretNamePrefix))
                    .OrderByDescending(f => File.GetCreationTimeUtc(f))
                    .ToList();

                if (secretFiles.Any())
                {
                    _currentKeyId = ExtractKeyIdFromSecretName(Path.GetFileName(secretFiles.First()));
                    return;
                }
            }

            // Try Docker API if in Swarm mode
            if (_isSwarmMode && _config.AllowApiWrites)
            {
                try
                {
                    var secrets = await ListSecretsFromApiAsync(cancellationToken);
                    var latestSecret = secrets
                        .Where(s => s.Spec?.Name?.StartsWith(_config.SecretNamePrefix) == true)
                        .OrderByDescending(s => s.CreatedAt)
                        .FirstOrDefault();

                    if (latestSecret != null)
                    {
                        _currentKeyId = ExtractKeyIdFromSecretName(latestSecret.Spec!.Name!);
                        return;
                    }
                }
                catch
                {

                    // Ignore API errors during initialization
                    System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                }
            }

            // Generate initial key if we can write via API
            if (_isSwarmMode && _config.AllowApiWrites)
            {
                _currentKeyId = Guid.NewGuid().ToString("N");
                await SaveKeyToStorage(_currentKeyId, GenerateKey(), CreateSystemContext());
            }
            else
            {
                _currentKeyId = "default";
            }
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? "default");
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            // Check filesystem access
            var fsHealthy = Directory.Exists(_config.SecretsPath);

            // Check Docker API if configured
            if (_dockerClient != null)
            {
                try
                {
                    var response = await _dockerClient.GetAsync("/_ping", cancellationToken);
                    return response.IsSuccessStatusCode || fsHealthy;
                }
                catch
                {
                    return fsHealthy;
                }
            }

            return fsHealthy;
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            var secretName = GetSecretName(keyId);

            // First, try to read from filesystem (preferred for containers)
            var secretPath = Path.Combine(_config.SecretsPath, secretName);
            if (File.Exists(secretPath))
            {
                return await File.ReadAllBytesAsync(secretPath);
            }

            // Fall back to Docker API (for out-of-container access)
            if (_isSwarmMode)
            {
                throw new KeyNotFoundException(
                    $"Docker secret '{secretName}' not found at '{secretPath}'. " +
                    "Note: Docker secrets can only be read from within a container where they are mounted. " +
                    "Ensure the secret is mounted to the service via docker-compose or stack deploy.");
            }

            throw new KeyNotFoundException(
                $"Secret '{keyId}' not found at '{secretPath}'. " +
                "Docker is not in Swarm mode, so secrets must be pre-mounted.");
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            if (!_isSwarmMode)
            {
                throw new NotSupportedException(
                    "Docker secret creation requires Swarm mode. " +
                    "Initialize a swarm with 'docker swarm init' or use a different key store strategy.");
            }

            if (!_config.AllowApiWrites)
            {
                throw new NotSupportedException(
                    "API writes are disabled. Set AllowApiWrites=true to enable secret creation via API.");
            }

            var secretName = GetSecretName(keyId);

            // Check if secret already exists
            var existingSecrets = await ListSecretsFromApiAsync(CancellationToken.None);
            var existingSecret = existingSecrets.FirstOrDefault(s => s.Spec?.Name == secretName);

            if (existingSecret != null)
            {
                // Docker secrets are immutable - create a new version
                var versionedName = $"{secretName}_v{DateTime.UtcNow:yyyyMMddHHmmss}";

                await CreateSecretViaApiAsync(versionedName, keyData, keyId, context);

                // Update service to use new secret (if needed, application handles this)
                System.Diagnostics.Trace.TraceInformation(
                    $"Created new secret version '{versionedName}'. " +
                    "Update your service to use the new secret.");
            }
            else
            {
                await CreateSecretViaApiAsync(secretName, keyData, keyId, context);
            }

            _currentKeyId = keyId;
        }

        private async Task CreateSecretViaApiAsync(string secretName, byte[] keyData, string keyId, ISecurityContext context)
        {
            var secretSpec = new DockerSecretSpec
            {
                Name = secretName,
                Data = Convert.ToBase64String(keyData),
                Labels = new Dictionary<string, string>
                {
                    [$"{_config.LabelPrefix}key-id"] = keyId,
                    [$"{_config.LabelPrefix}created-at"] = DateTime.UtcNow.ToString("O"),
                    [$"{_config.LabelPrefix}created-by"] = context.UserId,
                    [$"{_config.LabelPrefix}managed-by"] = "UltimateKeyManagement"
                }
            };

            var response = await _dockerClient!.PostAsJsonAsync("/secrets/create", secretSpec);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Failed to create Docker secret: {error}");
            }
        }

        private async Task<List<DockerSecret>> ListSecretsFromApiAsync(CancellationToken cancellationToken)
        {
            var labelFilter = Uri.EscapeDataString(
                JsonSerializer.Serialize(new { label = new[] { $"{_config.LabelPrefix}managed-by=UltimateKeyManagement" } }));

            var response = await _dockerClient!.GetAsync($"/secrets?filters={labelFilter}", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new List<DockerSecret>();
            }

            var secrets = await response.Content.ReadFromJsonAsync<List<DockerSecret>>(cancellationToken: cancellationToken);
            return secrets ?? new List<DockerSecret>();
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var keyIds = new HashSet<string>();

            // List from filesystem
            if (Directory.Exists(_config.SecretsPath))
            {
                var secretFiles = Directory.GetFiles(_config.SecretsPath)
                    .Where(f => Path.GetFileName(f).StartsWith(_config.SecretNamePrefix));

                foreach (var file in secretFiles)
                {
                    var keyId = ExtractKeyIdFromSecretName(Path.GetFileName(file));
                    if (!string.IsNullOrEmpty(keyId))
                    {
                        keyIds.Add(keyId);
                    }
                }
            }

            // List from Docker API if in Swarm mode
            if (_isSwarmMode)
            {
                try
                {
                    var secrets = await ListSecretsFromApiAsync(cancellationToken);
                    foreach (var secret in secrets)
                    {
                        if (secret.Spec?.Name?.StartsWith(_config.SecretNamePrefix) == true)
                        {
                            var keyId = ExtractKeyIdFromSecretName(secret.Spec.Name);
                            if (!string.IsNullOrEmpty(keyId))
                            {
                                keyIds.Add(keyId);
                            }
                        }
                    }
                }
                catch
                {

                    // Ignore API errors
                    System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                }
            }

            return keyIds.ToList().AsReadOnly();
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            if (!_isSwarmMode)
            {
                throw new NotSupportedException("Docker secret deletion requires Swarm mode.");
            }

            var secretName = GetSecretName(keyId);

            // Find the secret ID
            var secrets = await ListSecretsFromApiAsync(cancellationToken);
            var secret = secrets.FirstOrDefault(s => s.Spec?.Name == secretName);

            if (secret == null)
            {
                return; // Already deleted
            }

            var response = await _dockerClient!.DeleteAsync($"/secrets/{secret.Id}", cancellationToken);

            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Failed to delete Docker secret: {error}");
            }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var secretName = GetSecretName(keyId);

            // Check filesystem first
            var secretPath = Path.Combine(_config.SecretsPath, secretName);
            if (File.Exists(secretPath))
            {
                var fileInfo = new FileInfo(secretPath);
                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = fileInfo.CreationTimeUtc,
                    KeySizeBytes = (int)fileInfo.Length,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Source"] = "Filesystem",
                        ["Path"] = secretPath
                    }
                };
            }

            // Check Docker API
            if (_isSwarmMode)
            {
                var secrets = await ListSecretsFromApiAsync(cancellationToken);
                var secret = secrets.FirstOrDefault(s => s.Spec?.Name == secretName);

                if (secret != null)
                {
                    return new KeyMetadata
                    {
                        KeyId = keyId,
                        CreatedAt = secret.CreatedAt ?? DateTime.UtcNow,
                        CreatedBy = secret.Spec?.Labels?.GetValueOrDefault($"{_config.LabelPrefix}created-by"),
                        IsActive = keyId == _currentKeyId,
                        Version = (int)(secret.Version?.Index ?? 1),
                        Metadata = new Dictionary<string, object>
                        {
                            ["Source"] = "DockerAPI",
                            ["SecretId"] = secret.Id ?? "",
                            ["SecretName"] = secretName
                        }
                    };
                }
            }

            return null;
        }

        private string GetSecretName(string keyId)
        {
            // #3465: Sanitize keyId to prevent path traversal or injection.
            // Docker secret names may only contain [a-zA-Z0-9._-]; reject anything outside that set.
            if (string.IsNullOrWhiteSpace(keyId))
                throw new ArgumentException("Key ID must not be empty.", nameof(keyId));

            // Strip all characters that are not alphanumeric, dot, underscore, or hyphen.
            var safeName = System.Text.RegularExpressions.Regex
                .Replace(keyId.ToLowerInvariant(), @"[^a-z0-9._-]", "_");

            // Prevent directory traversal segments after sanitization.
            if (safeName.Contains(".."))
                throw new ArgumentException($"Key ID '{keyId}' resolves to an unsafe secret name.", nameof(keyId));

            return $"{_config.SecretNamePrefix}{safeName}";
        }

        private string ExtractKeyIdFromSecretName(string secretName)
        {
            // Handle versioned names like "prefix_keyid_v20240101120000"
            var name = secretName;
            if (name.Contains("_v20"))
            {
                name = name.Substring(0, name.LastIndexOf("_v20"));
            }

            if (name.StartsWith(_config.SecretNamePrefix))
            {
                return name.Substring(_config.SecretNamePrefix.Length);
            }
            return name;
        }

        public override void Dispose()
        {
            _dockerClient?.Dispose();
            base.Dispose();
        }

        #region Docker API DTOs

        private class DockerInfo
        {
            [JsonPropertyName("Swarm")]
            public SwarmInfo? Swarm { get; set; }
        }

        private class SwarmInfo
        {
            [JsonPropertyName("LocalNodeState")]
            public string? LocalNodeState { get; set; }
        }

        private class DockerSecret
        {
            [JsonPropertyName("ID")]
            public string? Id { get; set; }

            [JsonPropertyName("Version")]
            public DockerVersion? Version { get; set; }

            [JsonPropertyName("CreatedAt")]
            public DateTime? CreatedAt { get; set; }

            [JsonPropertyName("Spec")]
            public DockerSecretSpec? Spec { get; set; }
        }

        private class DockerVersion
        {
            [JsonPropertyName("Index")]
            public long Index { get; set; }
        }

        private class DockerSecretSpec
        {
            [JsonPropertyName("Name")]
            public string? Name { get; set; }

            [JsonPropertyName("Data")]
            public string? Data { get; set; }

            [JsonPropertyName("Labels")]
            public Dictionary<string, string>? Labels { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// Configuration for Docker Secrets-based key store strategy.
    /// </summary>
    public class DockerSecretsConfig
    {
        /// <summary>
        /// Base path where Docker secrets are mounted.
        /// Default: "/run/secrets"
        /// </summary>
        public string SecretsPath { get; set; } = "/run/secrets";

        /// <summary>
        /// Prefix for secret names.
        /// Default: "datawarehouse_key_"
        /// </summary>
        public string SecretNamePrefix { get; set; } = "datawarehouse_key_";

        /// <summary>
        /// Docker Engine API endpoint.
        /// Unix socket: "unix:///var/run/docker.sock"
        /// Windows named pipe: "npipe://./pipe/docker_engine"
        /// TCP: "http://localhost:2375" or "https://localhost:2376"
        /// </summary>
        public string DockerHost { get; set; } = Environment.OSVersion.Platform == PlatformID.Win32NT
            ? "npipe://./pipe/docker_engine"
            : "unix:///var/run/docker.sock";

        /// <summary>
        /// Label prefix for secret metadata.
        /// Default: "com.datawarehouse."
        /// </summary>
        public string LabelPrefix { get; set; } = "com.datawarehouse.";

        /// <summary>
        /// Allow creating/updating secrets via Docker API.
        /// Requires Swarm mode.
        /// Default: true
        /// </summary>
        public bool AllowApiWrites { get; set; } = true;

        /// <summary>
        /// Path to TLS client certificate (for TCP with TLS).
        /// </summary>
        public string? TlsCertPath { get; set; }

        /// <summary>
        /// Path to TLS client key (for TCP with TLS).
        /// </summary>
        public string? TlsKeyPath { get; set; }

        /// <summary>
        /// Path to TLS CA certificate (for TCP with TLS).
        /// </summary>
        public string? TlsCaPath { get; set; }
    }
}
