using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.CloudKms
{
    /// <summary>
    /// Alibaba Cloud KMS KeyStore strategy with HSM-backed envelope encryption.
    /// Implements IKeyStoreStrategy and IEnvelopeKeyStore for Alibaba Cloud integration.
    ///
    /// Supported features:
    /// - Alibaba Cloud KMS for key generation and envelope encryption
    /// - GenerateDataKey for DEK generation
    /// - Encrypt/Decrypt for envelope operations
    /// - AccessKey/SecretKey authentication
    /// - Key rotation support
    /// - HSM-backed key operations
    ///
    /// Configuration:
    /// - RegionId: Alibaba Cloud region (e.g., "cn-hangzhou", "ap-southeast-1")
    /// - AccessKeyId: Alibaba Cloud access key ID
    /// - AccessKeySecret: Alibaba Cloud access key secret
    /// - DefaultKeyId: Default KMS key ID for encryption
    /// </summary>
    public sealed class AlibabaKmsStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        // P2-3450: Share a single HttpClient per process to enable connection pooling
        // and proper DNS refresh via SocketsHttpHandler. Creating per-instance clients
        // leads to socket exhaustion under load.
        private static readonly HttpClient _httpClient = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        private AlibabaKmsConfig _config = new();
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
                ["Provider"] = "Alibaba Cloud KMS",
                ["Cloud"] = "Alibaba Cloud",
                ["SupportsCloudHsm"] = true,
                ["AuthMethod"] = "AccessKey/SecretKey"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("alibabakms.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[] { "AES-256-GCM", "AES_256" };

        public bool SupportsHsmKeyGeneration => true;

        public AlibabaKmsStrategy()
        {
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("alibabakms.init");
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("RegionId", out var regionObj) && regionObj is string region)
                _config.RegionId = region;
            if (Configuration.TryGetValue("AccessKeyId", out var accessKeyObj) && accessKeyObj is string accessKey)
                _config.AccessKeyId = accessKey;
            if (Configuration.TryGetValue("AccessKeySecret", out var secretKeyObj) && secretKeyObj is string secretKey)
                _config.AccessKeySecret = secretKey;
            if (Configuration.TryGetValue("DefaultKeyId", out var keyIdObj) && keyIdObj is string keyId)
                _config.DefaultKeyId = keyId;

            // Validate required configuration
            if (string.IsNullOrEmpty(_config.RegionId))
                throw new InvalidOperationException("RegionId is required for Alibaba KMS strategy");
            if (string.IsNullOrEmpty(_config.AccessKeyId))
                throw new InvalidOperationException("AccessKeyId is required for Alibaba KMS strategy");
            if (string.IsNullOrEmpty(_config.AccessKeySecret))
                throw new InvalidOperationException("AccessKeySecret is required for Alibaba KMS strategy");
            if (string.IsNullOrEmpty(_config.DefaultKeyId))
                throw new InvalidOperationException("DefaultKeyId is required for Alibaba KMS strategy");

            // Validate connection
            var isHealthy = await HealthCheckAsync(cancellationToken);
            if (!isHealthy)
            {
                throw new InvalidOperationException($"Cannot connect to Alibaba Cloud KMS in region {_config.RegionId}");
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
                var parameters = new Dictionary<string, string>
                {
                    ["Action"] = "DescribeKey",
                    ["KeyId"] = _config.DefaultKeyId
                };

                var request = CreateSignedRequest(parameters);
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
            // Alibaba Cloud KMS doesn't store keys externally - it generates data keys on demand
            // This method generates a new data key for the given key ID
            var parameters = new Dictionary<string, string>
            {
                ["Action"] = "GenerateDataKey",
                ["KeyId"] = keyId,
                ["KeySpec"] = "AES_256"
            };

            var request = CreateSignedRequest(parameters);
            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var plaintext = doc.RootElement.GetProperty("Plaintext").GetString();
            return Convert.FromBase64String(plaintext!);
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            // Alibaba Cloud KMS manages keys internally - we can create a new KMS key if needed
            var parameters = new Dictionary<string, string>
            {
                ["Action"] = "CreateKey",
                ["Description"] = $"DataWarehouse key: {keyId}",
                ["KeyUsage"] = "ENCRYPT/DECRYPT",
                ["Origin"] = "Aliyun_KMS",
                ["ProtectionLevel"] = "HSM"
            };

            var request = CreateSignedRequest(parameters);
            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var kmsKeyId = doc.RootElement.GetProperty("KeyMetadata").GetProperty("KeyId").GetString();

            _currentKeyId = kmsKeyId ?? keyId;
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var parameters = new Dictionary<string, string>
            {
                ["Action"] = "Encrypt",
                ["KeyId"] = kekId,
                ["Plaintext"] = Convert.ToBase64String(dataKey)
            };

            var request = CreateSignedRequest(parameters);
            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var ciphertext = doc.RootElement.GetProperty("CiphertextBlob").GetString();
            return Convert.FromBase64String(ciphertext!);
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var parameters = new Dictionary<string, string>
            {
                ["Action"] = "Decrypt",
                ["CiphertextBlob"] = Convert.ToBase64String(wrappedKey)
            };

            var request = CreateSignedRequest(parameters);
            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var plaintext = doc.RootElement.GetProperty("Plaintext").GetString();
            return Convert.FromBase64String(plaintext!);
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var parameters = new Dictionary<string, string>
            {
                ["Action"] = "ListKeys",
                ["PageSize"] = "100"
            };

            var request = CreateSignedRequest(parameters);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("Keys", out var keys) &&
                keys.TryGetProperty("Key", out var keyArray))
            {
                return keyArray.EnumerateArray()
                    .Select(k => k.GetProperty("KeyId").GetString() ?? "")
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

            // Alibaba Cloud KMS requires scheduling key deletion
            var parameters = new Dictionary<string, string>
            {
                ["Action"] = "ScheduleKeyDeletion",
                ["KeyId"] = keyId,
                ["PendingWindowInDays"] = "7" // Minimum is 7 days
            };

            var request = CreateSignedRequest(parameters);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            try
            {
                var parameters = new Dictionary<string, string>
                {
                    ["Action"] = "DescribeKey",
                    ["KeyId"] = keyId
                };

                var request = CreateSignedRequest(parameters);
                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var keyMetadata = doc.RootElement.GetProperty("KeyMetadata");

                var createdAt = keyMetadata.TryGetProperty("CreationDate", out var created)
                    ? DateTime.Parse(created.GetString()!)
                    : DateTime.UtcNow;

                var keyState = keyMetadata.TryGetProperty("KeyState", out var ks) ? ks.GetString() : "Unknown";

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = createdAt,
                    IsActive = keyState == "Enabled" && keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Region"] = _config.RegionId,
                        ["Backend"] = "Alibaba Cloud KMS",
                        ["KeyState"] = keyState ?? "",
                        ["KeyArn"] = keyMetadata.TryGetProperty("Arn", out var arn) ? arn.GetString() ?? "" : ""
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        private HttpRequestMessage CreateSignedRequest(Dictionary<string, string> parameters)
        {
            // Add common parameters
            parameters["Format"] = "JSON";
            parameters["Version"] = "2016-01-20";
            parameters["AccessKeyId"] = _config.AccessKeyId;
            parameters["SignatureMethod"] = "HMAC-SHA1";
            parameters["Timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            parameters["SignatureVersion"] = "1.0";
            parameters["SignatureNonce"] = Guid.NewGuid().ToString();

            // Sort parameters
            var sortedParams = parameters.OrderBy(p => p.Key, StringComparer.Ordinal);

            // Build canonical query string
            var canonicalQueryString = string.Join("&", sortedParams.Select(p =>
                $"{PercentEncode(p.Key)}={PercentEncode(p.Value)}"));

            // String to sign
            var stringToSign = $"POST&%2F&{PercentEncode(canonicalQueryString)}";

            // Calculate signature
            var signature = CalculateSignature(stringToSign);
            parameters["Signature"] = signature;

            // Build request URL
            var url = $"https://kms.{_config.RegionId}.aliyuncs.com/";

            // Create request
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            var content = string.Join("&", parameters.Select(p => $"{PercentEncode(p.Key)}={PercentEncode(p.Value)}"));
            request.Content = new StringContent(content, Encoding.UTF8, "application/x-www-form-urlencoded");

            return request;
        }

        /// <summary>
        /// Calculates signature using HMAC-SHA1 for Alibaba Cloud API authentication.
        /// </summary>
        /// <remarks>
        /// HMAC-SHA1 is required by Alibaba Cloud KMS API v1.0 protocol.
        /// This is a cloud provider API constraint, not a software design choice.
        /// The KMS service itself uses stronger algorithms for actual key operations.
        /// </remarks>
        private string CalculateSignature(string stringToSign)
        {
            var key = Encoding.UTF8.GetBytes($"{_config.AccessKeySecret}&");
            var data = Encoding.UTF8.GetBytes(stringToSign);
            using var hmac = new HMACSHA1(key);
            var hash = hmac.ComputeHash(data);
            return Convert.ToBase64String(hash);
        }

        private string PercentEncode(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var sb = new StringBuilder();
            foreach (var c in Encoding.UTF8.GetBytes(value))
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~')
                {
                    sb.Append((char)c);
                }
                else
                {
                    sb.Append('%').Append(c.ToString("X2"));
                }
            }
            return sb.ToString();
        }

        public override void Dispose()
        {
            // _httpClient is shared (static) — not disposed here to prevent breaking other callers.
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for Alibaba Cloud KMS key store strategy.
    /// </summary>
    public class AlibabaKmsConfig
    {
        public string RegionId { get; set; } = "cn-hangzhou";
        public string AccessKeyId { get; set; } = string.Empty;
        public string AccessKeySecret { get; set; } = string.Empty;
        public string DefaultKeyId { get; set; } = string.Empty;
    }
}
