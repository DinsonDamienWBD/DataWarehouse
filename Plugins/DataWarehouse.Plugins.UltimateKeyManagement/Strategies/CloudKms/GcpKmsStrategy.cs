using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.CloudKms
{
    /// <summary>
    /// Google Cloud KMS KeyStore strategy with HSM-backed envelope encryption.
    /// Implements IKeyStoreStrategy and IEnvelopeKeyStore for Google Cloud integration.
    ///
    /// Supported features:
    /// - Google Cloud KMS for key generation and envelope encryption
    /// - Project/Location/KeyRing/Key hierarchy
    /// - Encrypt/Decrypt for envelope operations
    /// - OAuth 2.0 authentication (Service Account or ADC)
    /// - Key rotation support
    /// - HSM-backed key operations (Cloud HSM integration)
    ///
    /// Configuration:
    /// - ProjectId: GCP project ID (e.g., "my-project")
    /// - Location: GCP location (e.g., "us-central1", "global")
    /// - KeyRing: Key ring name
    /// - KeyName: Key name within the key ring
    /// - ServiceAccountJson: Service account JSON (optional, uses ADC if not provided)
    /// </summary>
    public sealed class GcpKmsStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
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
        private GcpKmsConfig _config = new();
        private string? _currentKeyId;
        // #3459: Protect token fields with a dedicated lock to prevent race conditions.
        private readonly SemaphoreSlim _tokenLock = new(1, 1);
        private string? _accessToken;
        private DateTime _tokenExpiry = DateTime.MinValue;

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
                ["Provider"] = "Google Cloud KMS",
                ["Cloud"] = "Google Cloud Platform",
                ["SupportsCloudHsm"] = true,
                ["AuthMethod"] = "OAuth 2.0 (Service Account or ADC)"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("gcpkms.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[] { "AES-256-GCM", "GOOGLE_SYMMETRIC_ENCRYPTION" };

        public bool SupportsHsmKeyGeneration => true;

        public GcpKmsStrategy()
        {
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("gcpkms.init");
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("ProjectId", out var projectIdObj) && projectIdObj is string projectId)
                _config.ProjectId = projectId;
            if (Configuration.TryGetValue("Location", out var locationObj) && locationObj is string location)
                _config.Location = location;
            if (Configuration.TryGetValue("KeyRing", out var keyRingObj) && keyRingObj is string keyRing)
                _config.KeyRing = keyRing;
            if (Configuration.TryGetValue("KeyName", out var keyNameObj) && keyNameObj is string keyName)
                _config.KeyName = keyName;
            if (Configuration.TryGetValue("ServiceAccountJson", out var saJsonObj) && saJsonObj is string saJson)
                _config.ServiceAccountJson = saJson;

            // Validate required configuration
            if (string.IsNullOrEmpty(_config.ProjectId))
                throw new InvalidOperationException("ProjectId is required for GCP KMS strategy");
            if (string.IsNullOrEmpty(_config.Location))
                throw new InvalidOperationException("Location is required for GCP KMS strategy");
            if (string.IsNullOrEmpty(_config.KeyRing))
                throw new InvalidOperationException("KeyRing is required for GCP KMS strategy");
            if (string.IsNullOrEmpty(_config.KeyName))
                throw new InvalidOperationException("KeyName is required for GCP KMS strategy");

            // Authenticate
            await AuthenticateAsync(cancellationToken);

            // Validate connection
            var isHealthy = await HealthCheckAsync(cancellationToken);
            if (!isHealthy)
            {
                throw new InvalidOperationException($"Cannot connect to Google Cloud KMS in project {_config.ProjectId}");
            }

            _currentKeyId = GetKeyResourceName();

            await Task.CompletedTask;
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? GetKeyResourceName());
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await EnsureAuthenticatedAsync(cancellationToken);
                var keyResourceName = GetKeyResourceName();
                var request = CreateAuthenticatedRequest(HttpMethod.Get, $"https://cloudkms.googleapis.com/v1/{keyResourceName}");
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
            // #3453: Check if encrypted key file exists at storage path. If exists, decrypt via KMS.
            // If not, generate new key, encrypt with KMS, persist, then return.
            await EnsureAuthenticatedAsync(CancellationToken.None);

            var storagePath = _config.StoragePath;
            if (string.IsNullOrEmpty(storagePath))
                throw new InvalidOperationException(
                    "Key storage path not configured. Set GcpKmsConfig.StoragePath to enable key persistence.");

            var keyFilePath = GetGcpKeyFilePath(storagePath, keyId);
            var keyResourceName = GetKeyResourceName();

            if (File.Exists(keyFilePath))
            {
                // Load existing encrypted key and decrypt via KMS
                var encryptedKeyBase64 = await File.ReadAllTextAsync(keyFilePath);
                var decryptRequest = CreateAuthenticatedRequest(
                    HttpMethod.Post,
                    $"https://cloudkms.googleapis.com/v1/{keyResourceName}:decrypt",
                    new { ciphertext = encryptedKeyBase64.Trim() });

                var decryptResponse = await _httpClient.SendAsync(decryptRequest);
                decryptResponse.EnsureSuccessStatusCode();

                var decryptJson = await decryptResponse.Content.ReadAsStringAsync();
                using var decryptDoc = JsonDocument.Parse(decryptJson);
                var plaintext = decryptDoc.RootElement.GetProperty("plaintext").GetString();
                return Convert.FromBase64String(plaintext!);
            }

            // Key does not exist: generate, encrypt, persist, return
            var newKey = RandomNumberGenerator.GetBytes(32);
            var newKeyBase64 = Convert.ToBase64String(newKey);

            var encryptRequest = CreateAuthenticatedRequest(
                HttpMethod.Post,
                $"https://cloudkms.googleapis.com/v1/{keyResourceName}:encrypt",
                new { plaintext = newKeyBase64 });

            using var encryptResponse = await _httpClient.SendAsync(encryptRequest);
            encryptResponse.EnsureSuccessStatusCode();

            var encryptJson = await encryptResponse.Content.ReadAsStringAsync();
            using var encryptDoc = JsonDocument.Parse(encryptJson);
            var ciphertext = encryptDoc.RootElement.GetProperty("ciphertext").GetString();

            // Persist the KMS-encrypted key to storage
            if (!Directory.Exists(storagePath))
                Directory.CreateDirectory(storagePath);
            await File.WriteAllTextAsync(keyFilePath, ciphertext!);

            return newKey;
        }

        private static string GetGcpKeyFilePath(string storagePath, string keyId)
        {
            var safeId = Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(keyId)));
            return Path.Combine(storagePath, $"gcp-key-{safeId[..16]}.enc");
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            // GCP KMS manages keys internally - we can create a new key if needed
            await EnsureAuthenticatedAsync(CancellationToken.None);

            var keyRingPath = $"projects/{_config.ProjectId}/locations/{_config.Location}/keyRings/{_config.KeyRing}";

            var request = CreateAuthenticatedRequest(
                HttpMethod.Post,
                $"https://cloudkms.googleapis.com/v1/{keyRingPath}/cryptoKeys?cryptoKeyId={keyId}",
                new
                {
                    purpose = "ENCRYPT_DECRYPT",
                    versionTemplate = new
                    {
                        algorithm = "GOOGLE_SYMMETRIC_ENCRYPTION",
                        protectionLevel = "HSM"
                    }
                });

            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var name = doc.RootElement.GetProperty("name").GetString();

            _currentKeyId = name ?? keyId;
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);
            await EnsureAuthenticatedAsync(CancellationToken.None);

            var keyResourceName = string.IsNullOrEmpty(kekId) || kekId == GetKeyResourceName()
                ? GetKeyResourceName()
                : kekId;

            var request = CreateAuthenticatedRequest(
                HttpMethod.Post,
                $"https://cloudkms.googleapis.com/v1/{keyResourceName}:encrypt",
                new
                {
                    plaintext = Convert.ToBase64String(dataKey)
                });

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
            await EnsureAuthenticatedAsync(CancellationToken.None);

            var keyResourceName = string.IsNullOrEmpty(kekId) || kekId == GetKeyResourceName()
                ? GetKeyResourceName()
                : kekId;

            var request = CreateAuthenticatedRequest(
                HttpMethod.Post,
                $"https://cloudkms.googleapis.com/v1/{keyResourceName}:decrypt",
                new
                {
                    ciphertext = Convert.ToBase64String(wrappedKey)
                });

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
            await EnsureAuthenticatedAsync(cancellationToken);

            var keyRingPath = $"projects/{_config.ProjectId}/locations/{_config.Location}/keyRings/{_config.KeyRing}";
            var request = CreateAuthenticatedRequest(HttpMethod.Get, $"https://cloudkms.googleapis.com/v1/{keyRingPath}/cryptoKeys");
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("cryptoKeys", out var keys))
            {
                return keys.EnumerateArray()
                    .Select(k => k.GetProperty("name").GetString() ?? "")
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

            await EnsureAuthenticatedAsync(cancellationToken);

            // GCP KMS doesn't allow immediate deletion - keys are scheduled for destruction
            var keyResourceName = string.IsNullOrEmpty(keyId) || keyId == GetKeyResourceName()
                ? GetKeyResourceName()
                : keyId;

            // Get the primary version
            var getRequest = CreateAuthenticatedRequest(HttpMethod.Get, $"https://cloudkms.googleapis.com/v1/{keyResourceName}");
            var getResponse = await _httpClient.SendAsync(getRequest, cancellationToken);
            getResponse.EnsureSuccessStatusCode();

            var getJson = await getResponse.Content.ReadAsStringAsync(cancellationToken);
            using var getDoc = JsonDocument.Parse(getJson);
            var primaryVersion = getDoc.RootElement.GetProperty("primary").GetProperty("name").GetString();

            // Schedule destruction of the primary version
            var request = CreateAuthenticatedRequest(
                HttpMethod.Post,
                $"https://cloudkms.googleapis.com/v1/{primaryVersion}:destroy",
                new { });

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            try
            {
                await EnsureAuthenticatedAsync(cancellationToken);

                var keyResourceName = string.IsNullOrEmpty(keyId) || keyId == GetKeyResourceName()
                    ? GetKeyResourceName()
                    : keyId;

                var request = CreateAuthenticatedRequest(HttpMethod.Get, $"https://cloudkms.googleapis.com/v1/{keyResourceName}");
                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);

                var createdAt = doc.RootElement.TryGetProperty("createTime", out var created)
                    ? DateTime.Parse(created.GetString()!)
                    : DateTime.UtcNow;

                var primaryState = doc.RootElement.TryGetProperty("primary", out var primary) &&
                                  primary.TryGetProperty("state", out var state)
                    ? state.GetString()
                    : "UNKNOWN";

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = createdAt,
                    IsActive = primaryState == "ENABLED" && keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["ProjectId"] = _config.ProjectId,
                        ["Location"] = _config.Location,
                        ["Backend"] = "Google Cloud KMS",
                        ["KeyRing"] = _config.KeyRing,
                        ["PrimaryState"] = primaryState ?? ""
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        private string GetKeyResourceName()
        {
            return $"projects/{_config.ProjectId}/locations/{_config.Location}/keyRings/{_config.KeyRing}/cryptoKeys/{_config.KeyName}";
        }

        private async Task AuthenticateAsync(CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(_config.ServiceAccountJson))
            {
                // Parse service account JSON and get access token
                using var saDoc = JsonDocument.Parse(_config.ServiceAccountJson);
                var clientEmail = saDoc.RootElement.GetProperty("client_email").GetString();
                var privateKey = saDoc.RootElement.GetProperty("private_key").GetString();

                // Create JWT for OAuth 2.0
                var jwt = CreateServiceAccountJwt(clientEmail!, privateKey!);
                _accessToken = await ExchangeJwtForAccessToken(jwt, cancellationToken);
                _tokenExpiry = DateTime.UtcNow.AddMinutes(55); // Tokens are valid for 1 hour
            }
            else
            {
                // P2-3474: Application Default Credentials (ADC) — attempt GCE metadata server first.
                // On GCE/GKE the metadata server provides tokens without any service account JSON.
                var metadataUrl = "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token";
                var metadataRequest = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
                metadataRequest.Headers.Add("Metadata-Flavor", "Google");
                try
                {
                    using var metadataResponse = await _httpClient.SendAsync(metadataRequest, cancellationToken);
                    if (metadataResponse.IsSuccessStatusCode)
                    {
                        var metadataJson = await metadataResponse.Content.ReadAsStringAsync(cancellationToken);
                        using var doc = JsonDocument.Parse(metadataJson);
                        _accessToken = doc.RootElement.GetProperty("access_token").GetString();
                        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
                        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // Not on GCE — fall through to error
                }
                throw new InvalidOperationException(
                    "GCP KMS authentication failed: no ServiceAccountJson provided and GCE metadata server is unreachable. " +
                    "Provide 'ServiceAccountJson' in configuration or run on GCE/GKE for ADC.");
            }
        }

        private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
        {
            // #3459: Double-checked lock pattern to prevent concurrent token refresh races.
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry)
                return;

            await _tokenLock.WaitAsync(cancellationToken);
            try
            {
                if (string.IsNullOrEmpty(_accessToken) || DateTime.UtcNow >= _tokenExpiry)
                    await AuthenticateAsync(cancellationToken);
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        private string CreateServiceAccountJwt(string clientEmail, string privateKey)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = new { alg = "RS256", typ = "JWT" };
            var payload = new
            {
                iss = clientEmail,
                scope = "https://www.googleapis.com/auth/cloudkms",
                aud = "https://oauth2.googleapis.com/token",
                exp = now + 3600,
                iat = now
            };

            var headerJson = JsonSerializer.Serialize(header);
            var payloadJson = JsonSerializer.Serialize(payload);
            var headerBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(headerJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var signatureInput = $"{headerBase64}.{payloadBase64}";

            // Sign with RSA SHA256
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKey);
            var signature = rsa.SignData(Encoding.UTF8.GetBytes(signatureInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var signatureBase64 = Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            return $"{signatureInput}.{signatureBase64}";
        }

        private async Task<string> ExchangeJwtForAccessToken(string jwt, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token");
            request.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
                new KeyValuePair<string, string>("assertion", jwt)
            });

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("access_token").GetString()!;
        }

        private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string url, object? payload = null)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");

            if (payload != null)
            {
                var json = JsonSerializer.Serialize(payload);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            return request;
        }

        public override void Dispose()
        {
            // _httpClient is shared (static) — not disposed here to prevent breaking other callers.
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for Google Cloud KMS key store strategy.
    /// </summary>
    public class GcpKmsConfig
    {
        public string ProjectId { get; set; } = string.Empty;
        public string Location { get; set; } = "global";
        public string KeyRing { get; set; } = string.Empty;
        public string KeyName { get; set; } = string.Empty;
        public string ServiceAccountJson { get; set; } = string.Empty;
        /// <summary>
        /// Local directory for persisting KMS-encrypted data keys.
        /// Required for key persistence across restarts.
        /// </summary>
        public string? StoragePath { get; set; }
    }
}
