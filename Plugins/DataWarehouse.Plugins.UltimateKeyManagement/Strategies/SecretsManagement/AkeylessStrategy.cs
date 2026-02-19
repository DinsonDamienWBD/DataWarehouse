using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement
{
    /// <summary>
    /// Akeyless Vault KeyStore strategy supporting DFC (Data Field Cryptography) encryption.
    /// Implements IKeyStoreStrategy and IEnvelopeKeyStore for HSM-backed envelope encryption.
    ///
    /// Supported features:
    /// - Static secrets storage
    /// - DFC encryption/decryption (envelope encryption)
    /// - REST API authentication (access ID/key or token)
    /// - Path-based organization
    /// - Key rotation support
    /// - HSM-backed key operations (DFC encryption)
    ///
    /// Configuration:
    /// - GatewayUrl: Akeyless Gateway URL (e.g., "https://api.akeyless.io")
    /// - AccessId: Authentication access ID (optional if using token)
    /// - AccessKey: Authentication access key (optional if using token)
    /// - Token: Bearer token for authentication (optional if using AccessId/AccessKey)
    /// - Path: Base path for secrets (default: "/datawarehouse/keys")
    /// </summary>
    public sealed class AkeylessStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private readonly HttpClient _httpClient;
        private AkeylessConfig _config = new();
        private string? _currentKeyId;
        private string? _accessToken;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = true,
            SupportsHsm = true,
            SupportsExpiration = false,
            SupportsReplication = true,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "Akeyless",
                ["Backend"] = "Vault + DFC",
                ["SupportsDFC"] = true,
                ["AuthMethod"] = "Access ID/Key or Token"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("akeyless.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[] { "AES-256-GCM" };

        public bool SupportsHsmKeyGeneration => true;

        public AkeylessStrategy()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("akeyless.init");
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("GatewayUrl", out var urlObj) && urlObj is string url)
                _config.GatewayUrl = url;
            if (Configuration.TryGetValue("AccessId", out var accessIdObj) && accessIdObj is string accessId)
                _config.AccessId = accessId;
            if (Configuration.TryGetValue("AccessKey", out var accessKeyObj) && accessKeyObj is string accessKey)
                _config.AccessKey = accessKey;
            if (Configuration.TryGetValue("Token", out var tokenObj) && tokenObj is string token)
                _config.Token = token;
            if (Configuration.TryGetValue("Path", out var pathObj) && pathObj is string path)
                _config.Path = path;

            // Authenticate
            await AuthenticateAsync(cancellationToken);

            // Validate connection
            var isHealthy = await IsHealthyAsync(cancellationToken);
            if (!isHealthy)
            {
                throw new InvalidOperationException($"Cannot connect to Akeyless at {_config.GatewayUrl}");
            }

            _currentKeyId = "datawarehouse-master";

            await Task.CompletedTask;
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? "datawarehouse-master");
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            return await IsHealthyAsync(cancellationToken);
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            var secretPath = $"{_config.Path}/{keyId}";

            var payload = new
            {
                token = _accessToken,
                names = new[] { secretPath }
            };

            var content = JsonSerializer.Serialize(payload);
            var request = CreateRequest(HttpMethod.Post, "/get-secret-value");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(json);
            var keyBase64 = result.GetProperty(secretPath).GetString();
            return Convert.FromBase64String(keyBase64!);
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var keyBase64 = Convert.ToBase64String(keyData);
            var secretPath = $"{_config.Path}/{keyId}";

            var payload = new
            {
                token = _accessToken,
                name = secretPath,
                value = keyBase64
            };

            var content = JsonSerializer.Serialize(payload);
            var request = CreateRequest(HttpMethod.Post, "/create-secret");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            _currentKeyId = keyId;
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var payload = new
            {
                token = _accessToken,
                key_name = kekId,
                plaintext = Convert.ToBase64String(dataKey)
            };

            var content = JsonSerializer.Serialize(payload);
            var request = CreateRequest(HttpMethod.Post, "/encrypt");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(json);
            var ciphertext = result.GetProperty("ciphertext").GetString();
            return Encoding.UTF8.GetBytes(ciphertext!);
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var ciphertext = Encoding.UTF8.GetString(wrappedKey);
            var payload = new
            {
                token = _accessToken,
                key_name = kekId,
                ciphertext
            };

            var content = JsonSerializer.Serialize(payload);
            var request = CreateRequest(HttpMethod.Post, "/decrypt");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(json);
            var plaintext = result.GetProperty("plaintext").GetString();
            return Convert.FromBase64String(plaintext!);
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            try
            {
                var payload = new
                {
                    token = _accessToken,
                    path = _config.Path,
                    type = "static-secret"
                };

                var content = JsonSerializer.Serialize(payload);
                var request = CreateRequest(HttpMethod.Post, "/list-items");
                request.Content = new StringContent(content, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return Array.Empty<string>();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<JsonElement>(json);

                var keys = new List<string>();
                if (result.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.TryGetProperty("item_name", out var name))
                        {
                            var itemName = name.GetString() ?? "";
                            var prefix = $"{_config.Path}/";
                            if (itemName.StartsWith(prefix))
                            {
                                keys.Add(itemName.Substring(prefix.Length));
                            }
                        }
                    }
                }

                return keys.AsReadOnly();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            var secretPath = $"{_config.Path}/{keyId}";

            var payload = new
            {
                token = _accessToken,
                name = secretPath
            };

            var content = JsonSerializer.Serialize(payload);
            var request = CreateRequest(HttpMethod.Post, "/delete-item");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            try
            {
                var secretPath = $"{_config.Path}/{keyId}";

                var payload = new
                {
                    token = _accessToken,
                    name = secretPath
                };

                var content = JsonSerializer.Serialize(payload);
                var request = CreateRequest(HttpMethod.Post, "/describe-item");
                request.Content = new StringContent(content, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<JsonElement>(json);

                var createdAt = result.TryGetProperty("creation_date", out var cd)
                    ? DateTimeOffset.FromUnixTimeSeconds(cd.GetInt64()).DateTime
                    : DateTime.UtcNow;

                var version = result.TryGetProperty("item_version", out var iv)
                    ? iv.GetInt32()
                    : 1;

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = createdAt,
                    Version = version,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["SecretPath"] = secretPath,
                        ["Backend"] = "Akeyless Vault",
                        ["SupportsDFC"] = true
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        private async Task AuthenticateAsync(CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(_config.Token))
            {
                _accessToken = _config.Token;
                return;
            }

            if (string.IsNullOrEmpty(_config.AccessId) || string.IsNullOrEmpty(_config.AccessKey))
            {
                throw new InvalidOperationException("Either Token or AccessId/AccessKey must be provided for authentication.");
            }

            var payload = new
            {
                access_id = _config.AccessId,
                access_key = _config.AccessKey
            };

            var content = JsonSerializer.Serialize(payload);
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.GatewayUrl}/auth");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<JsonElement>(json);
            _accessToken = result.GetProperty("token").GetString();
        }

        private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.GatewayUrl}/status");
                var response = await _httpClient.SendAsync(request, cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, $"{_config.GatewayUrl}{path}");
            return request;
        }

        public override void Dispose()
        {
            _httpClient?.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for Akeyless Vault key store strategy.
    /// </summary>
    public class AkeylessConfig
    {
        public string GatewayUrl { get; set; } = "https://api.akeyless.io";
        public string AccessId { get; set; } = string.Empty;
        public string AccessKey { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string Path { get; set; } = "/datawarehouse/keys";
    }
}
