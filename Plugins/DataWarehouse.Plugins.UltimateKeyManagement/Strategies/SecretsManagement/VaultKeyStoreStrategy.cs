using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement
{
    /// <summary>
    /// HashiCorp Vault KeyStore strategy supporting KV secrets engine and Transit engine.
    /// Implements IKeyStoreStrategy and IEnvelopeKeyStore for HSM-backed envelope encryption.
    ///
    /// Supported features:
    /// - KV secrets engine for key storage
    /// - Transit engine for envelope encryption (wrap/unwrap operations)
    /// - Automatic token-based authentication
    /// - Key rotation support
    /// - HSM-backed key operations (Transit engine)
    ///
    /// Configuration:
    /// - Address: Vault server URL (e.g., "http://127.0.0.1:8200")
    /// - Token: Authentication token
    /// - MountPath: KV secrets engine mount path (default: "secret")
    /// - DefaultKeyName: Default key name for encryption (default: "datawarehouse-master")
    /// </summary>
    public sealed class VaultKeyStoreStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private readonly HttpClient _httpClient;
        private HashiCorpVaultConfig _config = new();
        private string? _currentKeyId;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = true,
            SupportsHsm = true,
            SupportsExpiration = false,
            SupportsReplication = false,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "HashiCorp Vault",
                ["Backend"] = "KV + Transit",
                ["SupportsTransitEngine"] = true,
                ["AuthMethod"] = "Token"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("vaultkeystore.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[] { "AES-256-GCM", "RSA-OAEP-256" };

        public bool SupportsHsmKeyGeneration => true;

        public VaultKeyStoreStrategy()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("vaultkeystore.init");
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("Address", out var addressObj) && addressObj is string address)
                _config.Address = address;
            if (Configuration.TryGetValue("Token", out var tokenObj) && tokenObj is string token)
                _config.Token = token;
            if (Configuration.TryGetValue("MountPath", out var mountObj) && mountObj is string mount)
                _config.MountPath = mount;
            if (Configuration.TryGetValue("DefaultKeyName", out var keyNameObj) && keyNameObj is string keyName)
                _config.DefaultKeyName = keyName;

            // Validate connection
            var isHealthy = await IsHealthyAsync();
            if (!isHealthy)
            {
                throw new InvalidOperationException($"Cannot connect to HashiCorp Vault at {_config.Address}");
            }

            _currentKeyId = _config.DefaultKeyName;

            await Task.CompletedTask;
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? _config.DefaultKeyName);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.Address}/v1/sys/health");
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            var request = CreateRequest(HttpMethod.Get, $"/v1/{_config.MountPath}/data/{keyId}");
            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var keyBase64 = doc.RootElement.GetProperty("data").GetProperty("data").GetProperty("key").GetString();
            return Convert.FromBase64String(keyBase64!);
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var keyBase64 = Convert.ToBase64String(keyData);

            var content = JsonSerializer.Serialize(new { data = new { key = keyBase64 } });
            var request = CreateRequest(HttpMethod.Post, $"/v1/{_config.MountPath}/data/{keyId}");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            _currentKeyId = keyId;
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var content = JsonSerializer.Serialize(new { plaintext = Convert.ToBase64String(dataKey) });
            var request = CreateRequest(HttpMethod.Post, $"/v1/transit/encrypt/{kekId}");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var ciphertext = doc.RootElement.GetProperty("data").GetProperty("ciphertext").GetString();
            return Encoding.UTF8.GetBytes(ciphertext!);
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var ciphertext = Encoding.UTF8.GetString(wrappedKey);
            var content = JsonSerializer.Serialize(new { ciphertext });
            var request = CreateRequest(HttpMethod.Post, $"/v1/transit/decrypt/{kekId}");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var plaintext = doc.RootElement.GetProperty("data").GetProperty("plaintext").GetString();
            return Convert.FromBase64String(plaintext!);
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var request = CreateRequest(HttpMethod.Get, $"/v1/{_config.MountPath}/metadata?list=true");
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("keys", out var keys))
            {
                return keys.EnumerateArray().Select(k => k.GetString() ?? "").ToList().AsReadOnly();
            }

            return Array.Empty<string>();
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            var request = CreateRequest(HttpMethod.Delete, $"/v1/{_config.MountPath}/metadata/{keyId}");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            try
            {
                var request = CreateRequest(HttpMethod.Get, $"/v1/{_config.MountPath}/metadata/{keyId}");
                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");

                var createdTime = data.TryGetProperty("created_time", out var ct)
                    ? DateTime.Parse(ct.GetString()!)
                    : DateTime.UtcNow;

                var currentVersion = data.TryGetProperty("current_version", out var cv)
                    ? cv.GetInt32()
                    : 1;

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = createdTime,
                    Version = currentVersion,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["VaultPath"] = $"{_config.MountPath}/{keyId}",
                        ["Backend"] = "HashiCorp Vault"
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        private async Task<bool> IsHealthyAsync()
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.Address}/v1/sys/health");
                using var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, $"{_config.Address}{path}");
            request.Headers.Add("X-Vault-Token", _config.Token);
            return request;
        }

        public override void Dispose()
        {
            _httpClient?.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for HashiCorp Vault key store strategy.
    /// </summary>
    public class HashiCorpVaultConfig
    {
        public string Address { get; set; } = "http://127.0.0.1:8200";
        public string Token { get; set; } = string.Empty;
        public string MountPath { get; set; } = "secret";
        public string DefaultKeyName { get; set; } = "datawarehouse-master";
    }
}
