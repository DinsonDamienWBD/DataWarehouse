using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.CloudKms
{
    /// <summary>
    /// Azure Key Vault KeyStore strategy with HSM-backed envelope encryption.
    /// Implements IKeyStoreStrategy and IEnvelopeKeyStore for Azure cloud integration.
    ///
    /// Supported features:
    /// - Azure Key Vault secrets for key storage
    /// - Azure Key Vault keys for envelope encryption (wrap/unwrap operations)
    /// - OAuth2 token-based authentication (client credentials flow)
    /// - Key rotation support
    /// - HSM-backed key operations (Premium tier)
    ///
    /// Configuration:
    /// - VaultUrl: Azure Key Vault URL (e.g., "https://{vault-name}.vault.azure.net")
    /// - TenantId: Azure AD tenant ID
    /// - ClientId: Application (client) ID
    /// - ClientSecret: Client secret
    /// - DefaultKeyName: Default key name for encryption (default: "datawarehouse-master")
    /// </summary>
    public sealed class AzureKeyVaultStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        // P2-3450: Shared static HttpClient to prevent socket exhaustion
        private static readonly HttpClient _httpClient = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        // P2-3444: serialize token refresh so concurrent callers don't all race to refresh.
        private readonly System.Threading.SemaphoreSlim _tokenRefreshLock = new(1, 1);
        private AzureKeyVaultConfig _config = new();
        private string? _accessToken;
        private DateTime _tokenExpiry;
        private string? _currentKeyId;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = true,
            SupportsHsm = true,
            SupportsExpiration = true,
            SupportsReplication = true,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "Azure Key Vault",
                ["Cloud"] = "Microsoft Azure",
                ["SupportsHsmTier"] = true,
                ["AuthMethod"] = "OAuth2 Client Credentials"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("azurekeyvault.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[] { "RSA-OAEP-256", "RSA-OAEP", "AES-256-GCM" };

        public bool SupportsHsmKeyGeneration => true;

        public AzureKeyVaultStrategy()
        {
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("azurekeyvault.init");
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("VaultUrl", out var vaultUrlObj) && vaultUrlObj is string vaultUrl)
                _config.VaultUrl = vaultUrl;
            if (Configuration.TryGetValue("TenantId", out var tenantIdObj) && tenantIdObj is string tenantId)
                _config.TenantId = tenantId;
            if (Configuration.TryGetValue("ClientId", out var clientIdObj) && clientIdObj is string clientId)
                _config.ClientId = clientId;
            if (Configuration.TryGetValue("ClientSecret", out var clientSecretObj) && clientSecretObj is string clientSecret)
                _config.ClientSecret = clientSecret;
            if (Configuration.TryGetValue("DefaultKeyName", out var keyNameObj) && keyNameObj is string keyName)
                _config.DefaultKeyName = keyName;

            // Validate connection by obtaining a token
            await EnsureTokenAsync(cancellationToken);

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
                await EnsureTokenAsync(cancellationToken);
                var request = CreateRequest(HttpMethod.Get, "/keys?api-version=7.4");
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
            await EnsureTokenAsync();
            var request = CreateRequest(HttpMethod.Get, $"/secrets/{keyId}?api-version=7.4");
            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var value = doc.RootElement.GetProperty("value").GetString();
            return Convert.FromBase64String(value!);
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            await EnsureTokenAsync();
            var keyBase64 = Convert.ToBase64String(keyData);

            var content = JsonSerializer.Serialize(new { value = keyBase64, contentType = "application/octet-stream" });
            var request = CreateRequest(HttpMethod.Put, $"/secrets/{keyId}?api-version=7.4");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            _currentKeyId = keyId;
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await EnsureTokenAsync();
            var content = JsonSerializer.Serialize(new { alg = "RSA-OAEP", value = Convert.ToBase64String(dataKey) });
            var request = CreateRequest(HttpMethod.Post, $"/keys/{kekId}/wrapkey?api-version=7.4");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var wrapped = doc.RootElement.GetProperty("value").GetString();
            return Convert.FromBase64String(wrapped!);
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await EnsureTokenAsync();
            var content = JsonSerializer.Serialize(new { alg = "RSA-OAEP", value = Convert.ToBase64String(wrappedKey) });
            var request = CreateRequest(HttpMethod.Post, $"/keys/{kekId}/unwrapkey?api-version=7.4");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var unwrapped = doc.RootElement.GetProperty("value").GetString();
            return Convert.FromBase64String(unwrapped!);
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            await EnsureTokenAsync(cancellationToken);
            var request = CreateRequest(HttpMethod.Get, "/secrets?api-version=7.4");
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                return value.EnumerateArray()
                    .Select(item => item.TryGetProperty("id", out var id) ? Path.GetFileName(id.GetString() ?? "") : "")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList()
                    .AsReadOnly();
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

            await EnsureTokenAsync(cancellationToken);
            var request = CreateRequest(HttpMethod.Delete, $"/secrets/{keyId}?api-version=7.4");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            try
            {
                await EnsureTokenAsync(cancellationToken);
                var request = CreateRequest(HttpMethod.Get, $"/secrets/{keyId}?api-version=7.4");
                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var attributes = doc.RootElement.GetProperty("attributes");

                var createdAt = attributes.TryGetProperty("created", out var created)
                    ? DateTimeOffset.FromUnixTimeSeconds(created.GetInt64()).UtcDateTime
                    : DateTime.UtcNow;

                var updatedAt = attributes.TryGetProperty("updated", out var updated)
                    ? DateTimeOffset.FromUnixTimeSeconds(updated.GetInt64()).UtcDateTime
                    : (DateTime?)null;

                var enabled = attributes.TryGetProperty("enabled", out var en) && en.GetBoolean();

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = createdAt,
                    LastRotatedAt = updatedAt,
                    IsActive = enabled && keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["VaultUrl"] = _config.VaultUrl,
                        ["Backend"] = "Azure Key Vault",
                        ["SecretId"] = doc.RootElement.GetProperty("id").GetString() ?? ""
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        private async Task EnsureTokenAsync(CancellationToken cancellationToken = default)
        {
            // Fast-path: token still valid — no lock needed for the read (double-checked locking).
            if (_accessToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
                return;

            // P2-3444: serialize token refresh to prevent concurrent callers from all refreshing
            // simultaneously when the token expires.
            await _tokenRefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-check inside the lock; another caller may have refreshed already.
                if (_accessToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
                    return;

            var tokenUrl = $"https://login.microsoftonline.com/{_config.TenantId}/oauth2/v2.0/token";
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _config.ClientId,
                ["client_secret"] = _config.ClientSecret,
                ["scope"] = "https://vault.azure.net/.default",
                ["grant_type"] = "client_credentials"
            });

            using var response = await _httpClient.PostAsync(tokenUrl, content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            _accessToken = doc.RootElement.GetProperty("access_token").GetString();
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);
            }
            finally
            {
                _tokenRefreshLock.Release();
            }
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, $"{_config.VaultUrl}{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            return request;
        }

        public override void Dispose()
        {
            // _httpClient is shared (static) — not disposed here to prevent breaking other callers.
            _tokenRefreshLock?.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for Azure Key Vault key store strategy.
    /// </summary>
    public class AzureKeyVaultConfig
    {
        public string VaultUrl { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string DefaultKeyName { get; set; } = "datawarehouse-master";
    }
}
