using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.CloudKms
{
    /// <summary>
    /// AWS KMS KeyStore strategy with HSM-backed envelope encryption.
    /// Implements IKeyStoreStrategy and IEnvelopeKeyStore for AWS cloud integration.
    ///
    /// Supported features:
    /// - AWS KMS for key generation and envelope encryption
    /// - GenerateDataKey for DEK generation
    /// - Encrypt/Decrypt for envelope operations
    /// - AWS Signature Version 4 authentication
    /// - Key rotation support
    /// - HSM-backed key operations (CloudHSM integration)
    ///
    /// Configuration:
    /// - Region: AWS region (e.g., "us-east-1")
    /// - AccessKeyId: AWS access key ID
    /// - SecretAccessKey: AWS secret access key
    /// - DefaultKeyId: Default KMS key ID for encryption (ARN or alias)
    /// </summary>
    public sealed class AwsKmsStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private readonly HttpClient _httpClient;
        private AwsKmsConfig _config = new();
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
                ["Provider"] = "AWS KMS",
                ["Cloud"] = "Amazon Web Services",
                ["SupportsCloudHsm"] = true,
                ["AuthMethod"] = "AWS Signature Version 4"
            }
        };

        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[] { "AES-256-GCM", "SYMMETRIC_DEFAULT" };

        public bool SupportsHsmKeyGeneration => true;

        public AwsKmsStrategy()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("Region", out var regionObj) && regionObj is string region)
                _config.Region = region;
            if (Configuration.TryGetValue("AccessKeyId", out var accessKeyObj) && accessKeyObj is string accessKey)
                _config.AccessKeyId = accessKey;
            if (Configuration.TryGetValue("SecretAccessKey", out var secretKeyObj) && secretKeyObj is string secretKey)
                _config.SecretAccessKey = secretKey;
            if (Configuration.TryGetValue("DefaultKeyId", out var keyIdObj) && keyIdObj is string keyId)
                _config.DefaultKeyId = keyId;

            // Validate connection
            var isHealthy = await HealthCheckAsync(cancellationToken);
            if (!isHealthy)
            {
                throw new InvalidOperationException($"Cannot connect to AWS KMS in region {_config.Region}");
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
                var request = CreateSignedRequest("DescribeKey", new { KeyId = _config.DefaultKeyId });
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
            // AWS KMS doesn't store keys externally - it generates data keys on demand
            // This method generates a new data key for the given key ID
            var request = CreateSignedRequest("GenerateDataKey", new
            {
                KeyId = keyId,
                KeySpec = "AES_256"
            });

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var plaintext = doc.RootElement.GetProperty("Plaintext").GetString();
            return Convert.FromBase64String(plaintext!);
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            // AWS KMS manages keys internally - we can create a new KMS key if needed
            var request = CreateSignedRequest("CreateKey", new
            {
                Description = $"DataWarehouse key: {keyId}",
                KeyUsage = "ENCRYPT_DECRYPT",
                KeySpec = "SYMMETRIC_DEFAULT"
            });

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var kmsKeyId = doc.RootElement.GetProperty("KeyMetadata").GetProperty("KeyId").GetString();

            _currentKeyId = kmsKeyId ?? keyId;
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var request = CreateSignedRequest("Encrypt", new
            {
                KeyId = kekId,
                Plaintext = Convert.ToBase64String(dataKey)
            });

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var ciphertext = doc.RootElement.GetProperty("CiphertextBlob").GetString();
            return Convert.FromBase64String(ciphertext!);
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var request = CreateSignedRequest("Decrypt", new
            {
                KeyId = kekId,
                CiphertextBlob = Convert.ToBase64String(wrappedKey)
            });

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var plaintext = doc.RootElement.GetProperty("Plaintext").GetString();
            return Convert.FromBase64String(plaintext!);
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var request = CreateSignedRequest("ListKeys", new { });
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("Keys", out var keys))
            {
                return keys.EnumerateArray()
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

            // AWS KMS requires scheduling key deletion (cannot be immediate)
            var request = CreateSignedRequest("ScheduleKeyDeletion", new
            {
                KeyId = keyId,
                PendingWindowInDays = 7 // Minimum is 7 days
            });

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            try
            {
                var request = CreateSignedRequest("DescribeKey", new { KeyId = keyId });
                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonDocument.Parse(json);
                var keyMetadata = doc.RootElement.GetProperty("KeyMetadata");

                var createdAt = keyMetadata.TryGetProperty("CreationDate", out var created)
                    ? DateTimeOffset.FromUnixTimeSeconds((long)created.GetDouble()).UtcDateTime
                    : DateTime.UtcNow;

                var enabled = keyMetadata.TryGetProperty("Enabled", out var en) && en.GetBoolean();

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = createdAt,
                    IsActive = enabled && keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Region"] = _config.Region,
                        ["Backend"] = "AWS KMS",
                        ["KeyArn"] = keyMetadata.GetProperty("Arn").GetString() ?? "",
                        ["KeyState"] = keyMetadata.TryGetProperty("KeyState", out var ks) ? ks.GetString() ?? "" : ""
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        private HttpRequestMessage CreateSignedRequest(string action, object payload)
        {
            var content = JsonSerializer.Serialize(payload);
            var request = new HttpRequestMessage(HttpMethod.Post, $"https://kms.{_config.Region}.amazonaws.com/");
            request.Headers.Add("X-Amz-Target", $"TrentService.{action}");
            request.Content = new StringContent(content, Encoding.UTF8, "application/x-amz-json-1.1");

            SignRequest(request, content, action);
            return request;
        }

        private void SignRequest(HttpRequestMessage request, string content, string action)
        {
            var now = DateTime.UtcNow;
            var dateStamp = now.ToString("yyyyMMdd");
            var amzDate = now.ToString("yyyyMMddTHHmmssZ");

            request.Headers.Add("X-Amz-Date", amzDate);
            request.Headers.Add("Host", $"kms.{_config.Region}.amazonaws.com");

            var canonicalRequest = CreateCanonicalRequest(request, content, amzDate, action);
            var stringToSign = CreateStringToSign(canonicalRequest, amzDate, dateStamp);
            var signature = CalculateSignature(stringToSign, dateStamp);

            var authHeader = $"AWS4-HMAC-SHA256 Credential={_config.AccessKeyId}/{dateStamp}/{_config.Region}/kms/aws4_request, " +
                           $"SignedHeaders=content-type;host;x-amz-date;x-amz-target, Signature={signature}";
            request.Headers.TryAddWithoutValidation("Authorization", authHeader);
        }

        private string CreateCanonicalRequest(HttpRequestMessage request, string content, string amzDate, string action)
        {
            var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            return $"POST\n/\n\ncontent-type:application/x-amz-json-1.1\nhost:kms.{_config.Region}.amazonaws.com\n" +
                   $"x-amz-date:{amzDate}\nx-amz-target:TrentService.{action}\n\n" +
                   $"content-type;host;x-amz-date;x-amz-target\n{contentHash}";
        }

        private string CreateStringToSign(string canonicalRequest, string amzDate, string dateStamp)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant();
            return $"AWS4-HMAC-SHA256\n{amzDate}\n{dateStamp}/{_config.Region}/kms/aws4_request\n{hash}";
        }

        private string CalculateSignature(string stringToSign, string dateStamp)
        {
            var kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes($"AWS4{_config.SecretAccessKey}"), Encoding.UTF8.GetBytes(dateStamp));
            var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(_config.Region));
            var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes("kms"));
            var kSigning = HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"));
            return Convert.ToHexString(HMACSHA256.HashData(kSigning, Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant();
        }

        public override void Dispose()
        {
            _httpClient?.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for AWS KMS key store strategy.
    /// </summary>
    public class AwsKmsConfig
    {
        public string Region { get; set; } = "us-east-1";
        public string AccessKeyId { get; set; } = string.Empty;
        public string SecretAccessKey { get; set; } = string.Empty;
        public string DefaultKeyId { get; set; } = string.Empty;
    }
}
