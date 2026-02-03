using DataWarehouse.SDK.Security;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Hsm
{
    /// <summary>
    /// Fortanix Data Security Manager (DSM) KeyStore strategy via REST API.
    /// Implements IKeyStoreStrategy and IEnvelopeKeyStore for cloud-native HSM key management.
    ///
    /// Fortanix DSM is a FIPS 140-2 Level 3 certified cloud-native HSM solution that
    /// provides hardware-grade security with software-defined flexibility. It uses
    /// Intel SGX enclaves for secure key operations.
    ///
    /// Supported features:
    /// - AES-256, RSA, and EC key generation within secure enclaves
    /// - Key wrapping/unwrapping (envelope encryption) with multiple algorithms
    /// - RESTful API interface (no PKCS#11 driver required)
    /// - Multi-tenant with groups and applications
    /// - Key versioning and rotation
    /// - Approval policies and quorum
    /// - Audit logging and SIEM integration
    /// - SaaS, on-premises, or hybrid deployment
    ///
    /// Configuration:
    /// - ApiEndpoint: Fortanix DSM API endpoint (default: https://sdkms.fortanix.com)
    /// - ApiKey: API key for authentication
    /// - GroupId: Group UUID containing the keys (optional)
    /// - DefaultKeyName: Default key name for encryption (default: "datawarehouse-master")
    ///
    /// Authentication Methods:
    /// 1. API Key: Simple token-based authentication
    /// 2. App Credentials: App UUID + secret for OAuth2 flow
    /// 3. Certificate: Client certificate authentication
    ///
    /// Example Configuration (API Key):
    /// {
    ///     "ApiEndpoint": "https://sdkms.fortanix.com",
    ///     "ApiKey": "your-api-key",
    ///     "GroupId": "group-uuid",
    ///     "DefaultKeyName": "dw-master-key"
    /// }
    ///
    /// Example Configuration (App Credentials):
    /// {
    ///     "ApiEndpoint": "https://sdkms.fortanix.com",
    ///     "AppUuid": "app-uuid",
    ///     "AppSecret": "app-secret",
    ///     "GroupId": "group-uuid",
    ///     "DefaultKeyName": "dw-master-key"
    /// }
    /// </summary>
    public sealed class FortanixDsmStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private readonly HttpClient _httpClient;
        private FortanixDsmConfig _config = new();
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
                ["Provider"] = "Fortanix Data Security Manager",
                ["Manufacturer"] = "Fortanix",
                ["Interface"] = "REST API",
                ["FipsLevel"] = "140-2 Level 3",
                ["SupportsIntelSgx"] = true,
                ["SupportsCloudNative"] = true,
                ["SupportsMultiTenant"] = true,
                ["SupportsApprovalPolicies"] = true,
                ["AuthMethods"] = new[] { "API Key", "OAuth2", "Certificate" }
            }
        };

        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[]
        {
            "AES",
            "AES-GCM",
            "RSA",
            "RSA-OAEP-SHA1",
            "RSA-OAEP-SHA256",
            "RSA-OAEP-SHA384",
            "RSA-OAEP-SHA512"
        };

        public bool SupportsHsmKeyGeneration => true;

        public FortanixDsmStrategy()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            // Load configuration
            if (Configuration.TryGetValue("ApiEndpoint", out var endpointObj) && endpointObj is string endpoint)
                _config.ApiEndpoint = endpoint.TrimEnd('/');
            if (Configuration.TryGetValue("ApiKey", out var apiKeyObj) && apiKeyObj is string apiKey)
                _config.ApiKey = apiKey;
            if (Configuration.TryGetValue("AppUuid", out var appUuidObj) && appUuidObj is string appUuid)
                _config.AppUuid = appUuid;
            if (Configuration.TryGetValue("AppSecret", out var appSecretObj) && appSecretObj is string appSecret)
                _config.AppSecret = appSecret;
            if (Configuration.TryGetValue("GroupId", out var groupIdObj) && groupIdObj is string groupId)
                _config.GroupId = groupId;
            if (Configuration.TryGetValue("DefaultKeyName", out var keyNameObj) && keyNameObj is string keyName)
                _config.DefaultKeyName = keyName;
            if (Configuration.TryGetValue("AccountId", out var accountIdObj) && accountIdObj is string accountId)
                _config.AccountId = accountId;

            // Authenticate
            await EnsureAuthenticatedAsync(cancellationToken);

            // Validate by listing keys
            var isHealthy = await HealthCheckAsync(cancellationToken);
            if (!isHealthy)
            {
                throw new InvalidOperationException($"Cannot connect to Fortanix DSM at {_config.ApiEndpoint}");
            }

            _currentKeyId = _config.DefaultKeyName;
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? _config.DefaultKeyName);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await EnsureAuthenticatedAsync(cancellationToken);
                var request = CreateRequest(HttpMethod.Get, "/sys/v1/health");
                var response = await _httpClient.SendAsync(request, cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            await EnsureAuthenticatedAsync();

            // Get the security object (key) by name
            var sobject = await GetSecurityObjectByNameAsync(keyId);
            if (sobject == null)
            {
                // Key doesn't exist - generate a new one
                var newKeyData = GenerateKey();
                await SaveKeyToStorage(keyId, newKeyData, context);
                return newKeyData;
            }

            // If key is exportable, we can retrieve its value
            if (sobject.Exportable)
            {
                var request = CreateRequest(HttpMethod.Post, $"/crypto/v1/keys/{sobject.Kid}/export");
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var exportResponse = JsonSerializer.Deserialize<DsmExportResponse>(json);
                return Convert.FromBase64String(exportResponse?.Value ?? throw new InvalidOperationException("Export failed"));
            }

            // Key is not exportable - this is the secure mode for HSM-stored keys
            throw new InvalidOperationException(
                $"Key '{keyId}' is stored in Fortanix DSM and is not exportable. " +
                "Use WrapKeyAsync/UnwrapKeyAsync for envelope encryption operations.");
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            await EnsureAuthenticatedAsync();

            // Check if key already exists
            var existing = await GetSecurityObjectByNameAsync(keyId);
            if (existing != null)
            {
                // Delete existing key
                await DeleteSecurityObjectAsync(existing.Kid);
            }

            // Create new security object (key)
            var createRequest = new DsmCreateKeyRequest
            {
                Name = keyId,
                ObjType = "AES",
                KeySize = keyData.Length * 8, // bits
                KeyOps = new[] { "ENCRYPT", "DECRYPT", "WRAPKEY", "UNWRAPKEY", "EXPORT" },
                Value = Convert.ToBase64String(keyData),
                Exportable = false, // Secure default
                Enabled = true
            };

            if (!string.IsNullOrEmpty(_config.GroupId))
            {
                createRequest.GroupId = _config.GroupId;
            }

            var request = CreateRequest(HttpMethod.Post, "/crypto/v1/keys");
            request.Content = new StringContent(
                JsonSerializer.Serialize(createRequest),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            _currentKeyId = keyId;
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);
            await EnsureAuthenticatedAsync();

            // Get KEK by name
            var kek = await GetSecurityObjectByNameAsync(kekId);
            if (kek == null)
            {
                throw new InvalidOperationException($"KEK with name '{kekId}' not found in Fortanix DSM");
            }

            // Prepare wrap request
            var wrapRequest = new DsmWrapRequest
            {
                Key = new DsmKeyReference { Kid = kek.Kid },
                Alg = GetWrappingAlgorithm(kek.ObjType),
                Plain = Convert.ToBase64String(dataKey),
                Mode = "CBC" // For AES wrapping
            };

            // Generate random IV for AES
            if (kek.ObjType == "AES")
            {
                var iv = new byte[16];
                using var rng = RandomNumberGenerator.Create();
                rng.GetBytes(iv);
                wrapRequest.Iv = Convert.ToBase64String(iv);
            }

            var request = CreateRequest(HttpMethod.Post, "/crypto/v1/wrapkey");
            request.Content = new StringContent(
                JsonSerializer.Serialize(wrapRequest),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var wrapResponse = JsonSerializer.Deserialize<DsmWrapResponse>(json);

            // Combine IV and wrapped key for storage
            var wrappedKey = Convert.FromBase64String(wrapResponse?.Wrapped ?? throw new InvalidOperationException("Wrap failed"));

            if (!string.IsNullOrEmpty(wrapRequest.Iv))
            {
                var iv = Convert.FromBase64String(wrapRequest.Iv);
                var result = new byte[iv.Length + wrappedKey.Length];
                Buffer.BlockCopy(iv, 0, result, 0, iv.Length);
                Buffer.BlockCopy(wrappedKey, 0, result, iv.Length, wrappedKey.Length);
                return result;
            }

            return wrappedKey;
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);
            await EnsureAuthenticatedAsync();

            // Get KEK by name
            var kek = await GetSecurityObjectByNameAsync(kekId);
            if (kek == null)
            {
                throw new InvalidOperationException($"KEK with name '{kekId}' not found in Fortanix DSM");
            }

            string? iv = null;
            byte[] ciphertext = wrappedKey;

            // Extract IV if present (for AES)
            if (kek.ObjType == "AES" && wrappedKey.Length > 16)
            {
                iv = Convert.ToBase64String(wrappedKey, 0, 16);
                ciphertext = new byte[wrappedKey.Length - 16];
                Buffer.BlockCopy(wrappedKey, 16, ciphertext, 0, ciphertext.Length);
            }

            // Prepare unwrap request
            var unwrapRequest = new DsmUnwrapRequest
            {
                Key = new DsmKeyReference { Kid = kek.Kid },
                Alg = GetWrappingAlgorithm(kek.ObjType),
                Wrapped = Convert.ToBase64String(ciphertext),
                Mode = "CBC",
                Iv = iv
            };

            var request = CreateRequest(HttpMethod.Post, "/crypto/v1/unwrapkey");
            request.Content = new StringContent(
                JsonSerializer.Serialize(unwrapRequest),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var unwrapResponse = JsonSerializer.Deserialize<DsmUnwrapResponse>(json);

            return Convert.FromBase64String(unwrapResponse?.Plain ?? throw new InvalidOperationException("Unwrap failed"));
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            await EnsureAuthenticatedAsync(cancellationToken);

            var url = "/crypto/v1/keys";
            if (!string.IsNullOrEmpty(_config.GroupId))
            {
                url += $"?group_id={_config.GroupId}";
            }

            var request = CreateRequest(HttpMethod.Get, url);
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var keys = JsonSerializer.Deserialize<List<DsmSecurityObject>>(json);

            return keys?.Select(k => k.Name).Where(n => !string.IsNullOrEmpty(n)).ToList().AsReadOnly()
                   ?? (IReadOnlyList<string>)Array.Empty<string>();
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            await EnsureAuthenticatedAsync(cancellationToken);

            var sobject = await GetSecurityObjectByNameAsync(keyId);
            if (sobject != null)
            {
                await DeleteSecurityObjectAsync(sobject.Kid);
            }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            await EnsureAuthenticatedAsync(cancellationToken);

            var sobject = await GetSecurityObjectByNameAsync(keyId);
            if (sobject == null)
                return null;

            return new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = sobject.CreatedAt ?? DateTime.UtcNow,
                KeySizeBytes = sobject.KeySize / 8,
                Version = sobject.Version,
                IsActive = sobject.Enabled && keyId == _currentKeyId,
                Metadata = new Dictionary<string, object>
                {
                    ["Provider"] = "Fortanix DSM",
                    ["Kid"] = sobject.Kid,
                    ["ObjType"] = sobject.ObjType ?? "",
                    ["GroupId"] = sobject.GroupId ?? "",
                    ["Exportable"] = sobject.Exportable,
                    ["KeyOps"] = string.Join(",", sobject.KeyOps ?? Array.Empty<string>())
                }
            };
        }

        private async Task<DsmSecurityObject?> GetSecurityObjectByNameAsync(string name)
        {
            var url = $"/crypto/v1/keys?name={Uri.EscapeDataString(name)}";
            if (!string.IsNullOrEmpty(_config.GroupId))
            {
                url += $"&group_id={_config.GroupId}";
            }

            var request = CreateRequest(HttpMethod.Get, url);
            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            var keys = JsonSerializer.Deserialize<List<DsmSecurityObject>>(json);

            return keys?.FirstOrDefault(k => k.Name == name);
        }

        private async Task DeleteSecurityObjectAsync(string kid)
        {
            var request = CreateRequest(HttpMethod.Delete, $"/crypto/v1/keys/{kid}");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
        {
            // Check if we have a valid token
            if (_accessToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
                return;

            if (!string.IsNullOrEmpty(_config.ApiKey))
            {
                // API Key authentication - use directly
                _accessToken = _config.ApiKey;
                _tokenExpiry = DateTime.UtcNow.AddHours(24); // API keys don't expire this way
                return;
            }

            if (!string.IsNullOrEmpty(_config.AppUuid) && !string.IsNullOrEmpty(_config.AppSecret))
            {
                // OAuth2 authentication
                var authRequest = new HttpRequestMessage(HttpMethod.Post, $"{_config.ApiEndpoint}/sys/v1/session/auth");

                // Basic auth header with app credentials
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_config.AppUuid}:{_config.AppSecret}"));
                authRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

                var response = await _httpClient.SendAsync(authRequest, cancellationToken);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var authResponse = JsonSerializer.Deserialize<DsmAuthResponse>(json);

                _accessToken = authResponse?.AccessToken;
                _tokenExpiry = DateTime.UtcNow.AddSeconds(authResponse?.ExpiresIn ?? 3600);
                return;
            }

            throw new InvalidOperationException("No valid authentication credentials configured for Fortanix DSM");
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, $"{_config.ApiEndpoint}{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            return request;
        }

        private static string GetWrappingAlgorithm(string? keyType)
        {
            return keyType?.ToUpperInvariant() switch
            {
                "AES" => "AES",
                "RSA" => "RSA-OAEP-SHA256",
                _ => "AES"
            };
        }

        public override void Dispose()
        {
            _httpClient?.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for Fortanix DSM key store strategy.
    /// </summary>
    public class FortanixDsmConfig
    {
        /// <summary>
        /// Fortanix DSM API endpoint URL.
        /// Default: https://sdkms.fortanix.com (SaaS)
        /// For on-premises: https://your-dsm-server:8443
        /// </summary>
        public string ApiEndpoint { get; set; } = "https://sdkms.fortanix.com";

        /// <summary>
        /// API Key for simple authentication.
        /// Generate in DSM console under Security Objects > API Keys.
        /// </summary>
        public string? ApiKey { get; set; }

        /// <summary>
        /// Application UUID for OAuth2 authentication.
        /// </summary>
        public string? AppUuid { get; set; }

        /// <summary>
        /// Application secret for OAuth2 authentication.
        /// </summary>
        public string? AppSecret { get; set; }

        /// <summary>
        /// Account ID (for multi-tenant deployments).
        /// </summary>
        public string? AccountId { get; set; }

        /// <summary>
        /// Group ID to scope key operations.
        /// Keys will be created in and listed from this group.
        /// </summary>
        public string? GroupId { get; set; }

        /// <summary>
        /// Default key name for encryption operations.
        /// </summary>
        public string DefaultKeyName { get; set; } = "datawarehouse-master";

        /// <summary>
        /// Request timeout in seconds.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Enable request retries on transient failures.
        /// </summary>
        public bool EnableRetry { get; set; } = true;

        /// <summary>
        /// Maximum retry attempts.
        /// </summary>
        public int MaxRetries { get; set; } = 3;
    }

    #region DSM API DTOs

    internal class DsmAuthResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    internal class DsmSecurityObject
    {
        [JsonPropertyName("kid")]
        public string Kid { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("obj_type")]
        public string? ObjType { get; set; }

        [JsonPropertyName("key_size")]
        public int KeySize { get; set; }

        [JsonPropertyName("key_ops")]
        public string[]? KeyOps { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("exportable")]
        public bool Exportable { get; set; }

        [JsonPropertyName("group_id")]
        public string? GroupId { get; set; }

        [JsonPropertyName("created_at")]
        public DateTime? CreatedAt { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;
    }

    internal class DsmCreateKeyRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("obj_type")]
        public string ObjType { get; set; } = "AES";

        [JsonPropertyName("key_size")]
        public int KeySize { get; set; } = 256;

        [JsonPropertyName("key_ops")]
        public string[]? KeyOps { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("exportable")]
        public bool Exportable { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("group_id")]
        public string? GroupId { get; set; }
    }

    internal class DsmKeyReference
    {
        [JsonPropertyName("kid")]
        public string? Kid { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    internal class DsmWrapRequest
    {
        [JsonPropertyName("key")]
        public DsmKeyReference? Key { get; set; }

        [JsonPropertyName("alg")]
        public string Alg { get; set; } = "AES";

        [JsonPropertyName("plain")]
        public string? Plain { get; set; }

        [JsonPropertyName("mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("iv")]
        public string? Iv { get; set; }
    }

    internal class DsmWrapResponse
    {
        [JsonPropertyName("wrapped")]
        public string? Wrapped { get; set; }

        [JsonPropertyName("iv")]
        public string? Iv { get; set; }
    }

    internal class DsmUnwrapRequest
    {
        [JsonPropertyName("key")]
        public DsmKeyReference? Key { get; set; }

        [JsonPropertyName("alg")]
        public string Alg { get; set; } = "AES";

        [JsonPropertyName("wrapped")]
        public string? Wrapped { get; set; }

        [JsonPropertyName("mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("iv")]
        public string? Iv { get; set; }
    }

    internal class DsmUnwrapResponse
    {
        [JsonPropertyName("plain")]
        public string? Plain { get; set; }
    }

    internal class DsmExportResponse
    {
        [JsonPropertyName("value")]
        public string? Value { get; set; }
    }

    #endregion
}
