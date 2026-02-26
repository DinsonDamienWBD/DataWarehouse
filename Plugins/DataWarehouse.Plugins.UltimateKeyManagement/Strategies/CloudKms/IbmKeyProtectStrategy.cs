using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.CloudKms
{
    /// <summary>
    /// IBM Key Protect KeyStore strategy with FIPS 140-2 Level 3 HSM-backed encryption.
    /// Implements IKeyStoreStrategy and IEnvelopeKeyStore for IBM Cloud integration.
    ///
    /// Supported features:
    /// - IBM Key Protect for key generation and envelope encryption
    /// - Wrap/Unwrap operations for DEK protection
    /// - FIPS 140-2 Level 3 certified HSMs
    /// - Key rotation and versioning support
    /// - Multi-region replication
    /// - IAM-based authentication
    ///
    /// Configuration:
    /// - InstanceId: IBM Cloud Key Protect instance ID (GUID)
    /// - Region: IBM Cloud region (e.g., "us-south", "eu-de")
    /// - ApiKey: IBM Cloud API key for authentication
    /// - DefaultKeyId: Default root key ID for encryption operations
    /// </summary>
    public sealed class IbmKeyProtectStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private readonly HttpClient _httpClient;
        private IbmKeyProtectConfig _config = new();
        private string? _currentKeyId;
        private string? _accessToken;
        private DateTime _tokenExpiry = DateTime.MinValue;

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
                ["Provider"] = "IBM Key Protect",
                ["Cloud"] = "IBM Cloud",
                ["FipsCompliance"] = "FIPS 140-2 Level 3",
                ["AuthMethod"] = "IBM Cloud IAM",
                ["HsmCertification"] = "FIPS 140-2 Level 3"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("ibmkeyprotect.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[] { "AES-256-GCM", "RSAES_OAEP_SHA_256" };

        public bool SupportsHsmKeyGeneration => true;

        public IbmKeyProtectStrategy()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("ibmkeyprotect.init");
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("InstanceId", out var instanceIdObj) && instanceIdObj is string instanceId)
                _config.InstanceId = instanceId;
            if (Configuration.TryGetValue("Region", out var regionObj) && regionObj is string region)
                _config.Region = region;
            if (Configuration.TryGetValue("ApiKey", out var apiKeyObj) && apiKeyObj is string apiKey)
                _config.ApiKey = apiKey;
            if (Configuration.TryGetValue("DefaultKeyId", out var keyIdObj) && keyIdObj is string keyId)
                _config.DefaultKeyId = keyId;
            if (Configuration.TryGetValue("StoragePath", out var storagePathObj) && storagePathObj is string storagePath)
                _config.StoragePath = storagePath;

            // Authenticate and get access token
            await RefreshAccessTokenAsync(cancellationToken);

            // Validate connection
            var isHealthy = await HealthCheckAsync(cancellationToken);
            if (!isHealthy)
            {
                throw new InvalidOperationException($"Cannot connect to IBM Key Protect in region {_config.Region}");
            }

            _currentKeyId = _config.DefaultKeyId;

            await Task.CompletedTask;
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? _config.DefaultKeyId);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await EnsureValidTokenAsync(cancellationToken);

                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://{_config.Region}.kms.cloud.ibm.com/api/v2/keys/{_config.DefaultKeyId}/metadata");
                request.Headers.Add("Authorization", $"Bearer {_accessToken}");
                request.Headers.Add("Bluemix-Instance", _config.InstanceId);

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
            // #3454: Load persisted wrapped key or generate a new one and persist it.
            await EnsureValidTokenAsync();

            var storagePath = _config.StoragePath;
            if (string.IsNullOrEmpty(storagePath))
                throw new InvalidOperationException(
                    "Key storage path not configured. Set IbmKeyProtectConfig.StoragePath to enable key persistence.");

            var keyFilePath = GetIbmKeyFilePath(storagePath, keyId);
            var kekId = string.IsNullOrEmpty(_config.DefaultKeyId) ? _currentKeyId : _config.DefaultKeyId;

            if (File.Exists(keyFilePath))
            {
                // Load wrapped key and unwrap via IBM Key Protect
                var wrappedKeyBase64 = await File.ReadAllTextAsync(keyFilePath);
                var wrappedKey = Convert.FromBase64String(wrappedKeyBase64.Trim());

                var unwrapPayload = new { ciphertext = Convert.ToBase64String(wrappedKey) };
                var unwrapRequest = new HttpRequestMessage(HttpMethod.Post,
                    $"https://{_config.Region}.kms.cloud.ibm.com/api/v2/keys/{kekId}/actions/unwrap");
                unwrapRequest.Headers.Add("Authorization", $"Bearer {_accessToken}");
                unwrapRequest.Headers.Add("Bluemix-Instance", _config.InstanceId);
                unwrapRequest.Content = new StringContent(
                    JsonSerializer.Serialize(unwrapPayload), Encoding.UTF8, "application/json");

                using var unwrapResponse = await _httpClient.SendAsync(unwrapRequest);
                unwrapResponse.EnsureSuccessStatusCode();

                var unwrapJson = await unwrapResponse.Content.ReadAsStringAsync();
                using var unwrapDoc = JsonDocument.Parse(unwrapJson);
                var plaintext = unwrapDoc.RootElement.GetProperty("plaintext").GetString();
                return Convert.FromBase64String(plaintext!);
            }

            // Generate new data key and wrap it
            var dataKey = new byte[32];
            RandomNumberGenerator.Fill(dataKey);

            var wrapPayload = new { plaintext = Convert.ToBase64String(dataKey) };
            var wrapRequest = new HttpRequestMessage(HttpMethod.Post,
                $"https://{_config.Region}.kms.cloud.ibm.com/api/v2/keys/{kekId}/actions/wrap");
            wrapRequest.Headers.Add("Authorization", $"Bearer {_accessToken}");
            wrapRequest.Headers.Add("Bluemix-Instance", _config.InstanceId);
            wrapRequest.Content = new StringContent(
                JsonSerializer.Serialize(wrapPayload), Encoding.UTF8, "application/json");

            using var wrapResponse = await _httpClient.SendAsync(wrapRequest);
            wrapResponse.EnsureSuccessStatusCode();

            var wrapJson = await wrapResponse.Content.ReadAsStringAsync();
            using var wrapDoc = JsonDocument.Parse(wrapJson);
            var ciphertext = wrapDoc.RootElement.GetProperty("ciphertext").GetString();

            // Persist wrapped key
            if (!Directory.Exists(storagePath))
                Directory.CreateDirectory(storagePath);
            await File.WriteAllTextAsync(keyFilePath, ciphertext!);

            return dataKey;
        }

        private static string GetIbmKeyFilePath(string storagePath, string keyId)
        {
            var safeId = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(keyId)));
            return Path.Combine(storagePath, $"ibm-key-{safeId[..16]}.enc");
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            await EnsureValidTokenAsync();

            // Create a new root key in IBM Key Protect
            var payload = new
            {
                metadata = new
                {
                    collectionType = "application/vnd.ibm.kms.key+json",
                    collectionTotal = 1
                },
                resources = new[]
                {
                    new
                    {
                        type = "application/vnd.ibm.kms.key+json",
                        name = $"DataWarehouse-{keyId}",
                        description = $"DataWarehouse encryption key: {keyId}",
                        extractable = false // Root key for wrapping
                    }
                }
            };

            var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://{_config.Region}.kms.cloud.ibm.com/api/v2/keys");
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");
            request.Headers.Add("Bluemix-Instance", _config.InstanceId);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var resources = doc.RootElement.GetProperty("resources");
            var newKeyId = resources[0].GetProperty("id").GetString();

            _currentKeyId = newKeyId ?? keyId;
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);
            await EnsureValidTokenAsync();

            var payload = new
            {
                plaintext = Convert.ToBase64String(dataKey)
            };

            var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://{_config.Region}.kms.cloud.ibm.com/api/v2/keys/{kekId}/actions/wrap");
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");
            request.Headers.Add("Bluemix-Instance", _config.InstanceId);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var ciphertext = doc.RootElement.GetProperty("ciphertext").GetString();
            return Convert.FromBase64String(ciphertext!);
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);
            await EnsureValidTokenAsync();

            var payload = new
            {
                ciphertext = Convert.ToBase64String(wrappedKey)
            };

            var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://{_config.Region}.kms.cloud.ibm.com/api/v2/keys/{kekId}/actions/unwrap");
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");
            request.Headers.Add("Bluemix-Instance", _config.InstanceId);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var plaintext = doc.RootElement.GetProperty("plaintext").GetString();
            return Convert.FromBase64String(plaintext!);
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            await EnsureValidTokenAsync(cancellationToken);

            var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://{_config.Region}.kms.cloud.ibm.com/api/v2/keys");
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");
            request.Headers.Add("Bluemix-Instance", _config.InstanceId);

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("resources", out var resources))
            {
                return resources.EnumerateArray()
                    .Select(k => k.GetProperty("id").GetString() ?? "")
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

            await EnsureValidTokenAsync(cancellationToken);

            var request = new HttpRequestMessage(HttpMethod.Delete,
                $"https://{_config.Region}.kms.cloud.ibm.com/api/v2/keys/{keyId}");
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");
            request.Headers.Add("Bluemix-Instance", _config.InstanceId);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            try
            {
                await EnsureValidTokenAsync(cancellationToken);

                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://{_config.Region}.kms.cloud.ibm.com/api/v2/keys/{keyId}/metadata");
                request.Headers.Add("Authorization", $"Bearer {_accessToken}");
                request.Headers.Add("Bluemix-Instance", _config.InstanceId);

                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var resources = doc.RootElement.GetProperty("resources");
                var keyMetadata = resources[0];

                var createdAt = keyMetadata.TryGetProperty("creationDate", out var created)
                    ? DateTime.Parse(created.GetString()!)
                    : DateTime.UtcNow;

                var state = keyMetadata.TryGetProperty("state", out var st) ? st.GetInt32() : 0;
                var isActive = state == 1; // 1 = Active in IBM Key Protect

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = createdAt,
                    IsActive = isActive && keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Region"] = _config.Region,
                        ["Backend"] = "IBM Key Protect",
                        ["InstanceId"] = _config.InstanceId,
                        ["State"] = state,
                        ["Extractable"] = keyMetadata.TryGetProperty("extractable", out var ex) && ex.GetBoolean()
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        private async Task RefreshAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://iam.cloud.ibm.com/identity/token");
            var formData = new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ibm:params:oauth:grant-type:apikey",
                ["apikey"] = _config.ApiKey
            };
            tokenRequest.Content = new FormUrlEncodedContent(formData);

            using var response = await _httpClient.SendAsync(tokenRequest, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            _accessToken = doc.RootElement.GetProperty("access_token").GetString();
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 300); // Refresh 5 minutes early
        }

        private async Task EnsureValidTokenAsync(CancellationToken cancellationToken = default)
        {
            if (DateTime.UtcNow >= _tokenExpiry || string.IsNullOrEmpty(_accessToken))
            {
                await RefreshAccessTokenAsync(cancellationToken);
            }
        }

        public override void Dispose()
        {
            _httpClient?.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for IBM Key Protect key store strategy.
    /// </summary>
    public class IbmKeyProtectConfig
    {
        public string InstanceId { get; set; } = string.Empty;
        public string Region { get; set; } = "us-south";
        public string ApiKey { get; set; } = string.Empty;
        public string DefaultKeyId { get; set; } = string.Empty;
        /// <summary>
        /// Local directory path for persisting wrapped data keys.
        /// Required for key persistence across restarts.
        /// </summary>
        public string? StoragePath { get; set; }
    }
}
