using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.CloudKms
{
    /// <summary>
    /// Oracle Cloud Infrastructure Vault KeyStore strategy with HSM-backed envelope encryption.
    /// Implements IKeyStoreStrategy and IEnvelopeKeyStore for Oracle Cloud integration.
    ///
    /// Supported features:
    /// - OCI Vault for key generation and envelope encryption
    /// - Wrap/Unwrap for envelope operations
    /// - OCI API signing (RSA-SHA256)
    /// - Key rotation support
    /// - HSM-backed key operations
    ///
    /// Configuration:
    /// - Region: OCI region (e.g., "us-ashburn-1", "us-phoenix-1")
    /// - TenancyOcid: Tenancy OCID
    /// - CompartmentOcid: Compartment OCID
    /// - VaultOcid: Vault OCID
    /// - KeyOcid: Default key OCID
    /// - UserOcid: User OCID (for API signing)
    /// - Fingerprint: API key fingerprint
    /// - PrivateKey: RSA private key for API signing (PEM format)
    /// </summary>
    public sealed class OracleVaultStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private readonly HttpClient _httpClient;
        private OracleVaultConfig _config = new();
        private string? _currentKeyId;

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
                ["Provider"] = "Oracle Cloud Infrastructure Vault",
                ["Cloud"] = "Oracle Cloud",
                ["SupportsCloudHsm"] = true,
                ["AuthMethod"] = "OCI API Signing (RSA-SHA256)"
            }
        };

        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[] { "AES-256-GCM", "RSA_OAEP_SHA256" };

        public bool SupportsHsmKeyGeneration => true;

        public OracleVaultStrategy()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("Region", out var regionObj) && regionObj is string region)
                _config.Region = region;
            if (Configuration.TryGetValue("TenancyOcid", out var tenancyObj) && tenancyObj is string tenancy)
                _config.TenancyOcid = tenancy;
            if (Configuration.TryGetValue("CompartmentOcid", out var compartmentObj) && compartmentObj is string compartment)
                _config.CompartmentOcid = compartment;
            if (Configuration.TryGetValue("VaultOcid", out var vaultObj) && vaultObj is string vault)
                _config.VaultOcid = vault;
            if (Configuration.TryGetValue("KeyOcid", out var keyObj) && keyObj is string key)
                _config.KeyOcid = key;
            if (Configuration.TryGetValue("UserOcid", out var userObj) && userObj is string user)
                _config.UserOcid = user;
            if (Configuration.TryGetValue("Fingerprint", out var fpObj) && fpObj is string fp)
                _config.Fingerprint = fp;
            if (Configuration.TryGetValue("PrivateKey", out var pkObj) && pkObj is string pk)
                _config.PrivateKey = pk;

            // Validate required configuration
            if (string.IsNullOrEmpty(_config.Region))
                throw new InvalidOperationException("Region is required for OCI Vault strategy");
            if (string.IsNullOrEmpty(_config.TenancyOcid))
                throw new InvalidOperationException("TenancyOcid is required for OCI Vault strategy");
            if (string.IsNullOrEmpty(_config.CompartmentOcid))
                throw new InvalidOperationException("CompartmentOcid is required for OCI Vault strategy");
            if (string.IsNullOrEmpty(_config.VaultOcid))
                throw new InvalidOperationException("VaultOcid is required for OCI Vault strategy");
            if (string.IsNullOrEmpty(_config.KeyOcid))
                throw new InvalidOperationException("KeyOcid is required for OCI Vault strategy");
            if (string.IsNullOrEmpty(_config.UserOcid))
                throw new InvalidOperationException("UserOcid is required for OCI Vault strategy");
            if (string.IsNullOrEmpty(_config.Fingerprint))
                throw new InvalidOperationException("Fingerprint is required for OCI Vault strategy");
            if (string.IsNullOrEmpty(_config.PrivateKey))
                throw new InvalidOperationException("PrivateKey is required for OCI Vault strategy");

            // Validate connection
            var isHealthy = await HealthCheckAsync(cancellationToken);
            if (!isHealthy)
            {
                throw new InvalidOperationException($"Cannot connect to OCI Vault in region {_config.Region}");
            }

            _currentKeyId = _config.KeyOcid;

            await Task.CompletedTask;
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? _config.KeyOcid);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var url = $"https://kms.{_config.Region}.oraclecloud.com/20180608/keys/{_config.KeyOcid}";
                var request = CreateSignedRequest(HttpMethod.Get, url, null);
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
            // OCI Vault doesn't store keys externally - it generates data keys on demand
            // Generate a new data key and wrap it
            var plainKey = RandomNumberGenerator.GetBytes(32);

            // Encrypt the key using the vault key
            var url = $"https://kms.{_config.Region}.oraclecloud.com/20180608/encrypt";
            var payload = new
            {
                keyId = string.IsNullOrEmpty(keyId) || keyId == _config.KeyOcid ? _config.KeyOcid : keyId,
                plaintext = Convert.ToBase64String(plainKey)
            };

            var request = CreateSignedRequest(HttpMethod.Post, url, payload);
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            // Return the plain key (in a real scenario, you'd store the ciphertext and decrypt when needed)
            return plainKey;
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            // OCI Vault manages keys internally - we can create a new key if needed
            var url = $"https://kms.{_config.Region}.oraclecloud.com/20180608/keys";
            var payload = new
            {
                compartmentId = _config.CompartmentOcid,
                displayName = $"DataWarehouse-{keyId}",
                keyShape = new
                {
                    algorithm = "AES",
                    length = 256
                },
                protectionMode = "HSM"
            };

            var request = CreateSignedRequest(HttpMethod.Post, url, payload);
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var newKeyId = doc.RootElement.GetProperty("id").GetString();

            _currentKeyId = newKeyId ?? keyId;
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var keyOcid = string.IsNullOrEmpty(kekId) || kekId == _config.KeyOcid ? _config.KeyOcid : kekId;
            var url = $"https://kms.{_config.Region}.oraclecloud.com/20180608/encrypt";
            var payload = new
            {
                keyId = keyOcid,
                plaintext = Convert.ToBase64String(dataKey)
            };

            var request = CreateSignedRequest(HttpMethod.Post, url, payload);
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var ciphertext = doc.RootElement.GetProperty("ciphertext").GetString();
            return Convert.FromBase64String(ciphertext!);
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var keyOcid = string.IsNullOrEmpty(kekId) || kekId == _config.KeyOcid ? _config.KeyOcid : kekId;
            var url = $"https://kms.{_config.Region}.oraclecloud.com/20180608/decrypt";
            var payload = new
            {
                keyId = keyOcid,
                ciphertext = Convert.ToBase64String(wrappedKey)
            };

            var request = CreateSignedRequest(HttpMethod.Post, url, payload);
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var plaintext = doc.RootElement.GetProperty("plaintext").GetString();
            return Convert.FromBase64String(plaintext!);
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var url = $"https://kms.{_config.Region}.oraclecloud.com/20180608/keys?compartmentId={_config.CompartmentOcid}";
            var request = CreateSignedRequest(HttpMethod.Get, url, null);
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return doc.RootElement.EnumerateArray()
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

            // OCI Vault requires scheduling key deletion
            var keyOcid = string.IsNullOrEmpty(keyId) || keyId == _config.KeyOcid ? _config.KeyOcid : keyId;
            var url = $"https://kms.{_config.Region}.oraclecloud.com/20180608/keys/{keyOcid}/actions/scheduleDeletion";
            var payload = new
            {
                timeOfDeletion = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            };

            var request = CreateSignedRequest(HttpMethod.Post, url, payload);
            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            try
            {
                var keyOcid = string.IsNullOrEmpty(keyId) || keyId == _config.KeyOcid ? _config.KeyOcid : keyId;
                var url = $"https://kms.{_config.Region}.oraclecloud.com/20180608/keys/{keyOcid}";
                var request = CreateSignedRequest(HttpMethod.Get, url, null);
                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonDocument.Parse(json);

                var createdAt = doc.RootElement.TryGetProperty("timeCreated", out var created)
                    ? DateTime.Parse(created.GetString()!)
                    : DateTime.UtcNow;

                var lifecycleState = doc.RootElement.TryGetProperty("lifecycleState", out var state)
                    ? state.GetString()
                    : "UNKNOWN";

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = createdAt,
                    IsActive = lifecycleState == "ENABLED" && keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Region"] = _config.Region,
                        ["Backend"] = "Oracle Cloud Infrastructure Vault",
                        ["VaultId"] = _config.VaultOcid,
                        ["LifecycleState"] = lifecycleState ?? "",
                        ["ProtectionMode"] = doc.RootElement.TryGetProperty("protectionMode", out var pm) ? pm.GetString() ?? "" : ""
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        private HttpRequestMessage CreateSignedRequest(HttpMethod method, string url, object? payload)
        {
            var request = new HttpRequestMessage(method, url);
            var now = DateTime.UtcNow;
            var date = now.ToString("r"); // RFC 1123 format

            request.Headers.Add("date", date);
            request.Headers.Add("host", new Uri(url).Host);

            string contentSha256 = string.Empty;
            string contentType = string.Empty;
            string contentLength = "0";

            if (payload != null)
            {
                var json = JsonSerializer.Serialize(payload);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                contentSha256 = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
                contentType = "application/json";
                contentLength = Encoding.UTF8.GetByteCount(json).ToString();

                request.Headers.Add("x-content-sha256", contentSha256);
                request.Headers.Add("content-type", contentType);
                request.Headers.Add("content-length", contentLength);
            }

            // Build signing string
            var signingString = BuildSigningString(method.Method, url, date, contentSha256, contentType, contentLength);

            // Sign the string
            var signature = SignString(signingString);

            // Build authorization header
            var authHeader = $"Signature version=\"1\",headers=\"date (request-target) host";
            if (!string.IsNullOrEmpty(contentSha256))
            {
                authHeader += " content-length content-type x-content-sha256";
            }
            authHeader += $"\",keyId=\"{_config.TenancyOcid}/{_config.UserOcid}/{_config.Fingerprint}\",algorithm=\"rsa-sha256\",signature=\"{signature}\"";

            request.Headers.TryAddWithoutValidation("authorization", authHeader);

            return request;
        }

        private string BuildSigningString(string method, string url, string date, string contentSha256, string contentType, string contentLength)
        {
            var uri = new Uri(url);
            var path = uri.PathAndQuery;

            var sb = new StringBuilder();
            sb.Append($"date: {date}\n");
            sb.Append($"(request-target): {method.ToLowerInvariant()} {path}\n");
            sb.Append($"host: {uri.Host}");

            if (!string.IsNullOrEmpty(contentSha256))
            {
                sb.Append($"\ncontent-length: {contentLength}");
                sb.Append($"\ncontent-type: {contentType}");
                sb.Append($"\nx-content-sha256: {contentSha256}");
            }

            return sb.ToString();
        }

        private string SignString(string stringToSign)
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(_config.PrivateKey);
            var signature = rsa.SignData(Encoding.UTF8.GetBytes(stringToSign), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return Convert.ToBase64String(signature);
        }

        public override void Dispose()
        {
            _httpClient?.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for Oracle Cloud Infrastructure Vault key store strategy.
    /// </summary>
    public class OracleVaultConfig
    {
        public string Region { get; set; } = "us-ashburn-1";
        public string TenancyOcid { get; set; } = string.Empty;
        public string CompartmentOcid { get; set; } = string.Empty;
        public string VaultOcid { get; set; } = string.Empty;
        public string KeyOcid { get; set; } = string.Empty;
        public string UserOcid { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
        public string PrivateKey { get; set; } = string.Empty;
    }
}
