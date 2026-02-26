using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Security.KeyManagement
{
    // ──────────────────────────────────────────────────────────────
    // GCP KMS Provider — ADC-based key management via REST API
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Configuration for GCP KMS provider.
    /// </summary>
    public sealed class GcpKmsConfig
    {
        /// <summary>GCP project ID.</summary>
        public string ProjectId { get; set; } = string.Empty;

        /// <summary>KMS location (e.g., "us-east1", "global").</summary>
        public string Location { get; set; } = "global";

        /// <summary>KMS key ring name.</summary>
        public string KeyRing { get; set; } = string.Empty;

        /// <summary>Default crypto key name within the key ring.</summary>
        public string DefaultCryptoKey { get; set; } = "default";

        /// <summary>Path to service account JSON (overrides GOOGLE_APPLICATION_CREDENTIALS).</summary>
        public string? ServiceAccountJsonPath { get; set; }

        /// <summary>Token refresh buffer — refresh this many minutes before expiry. Default 5.</summary>
        public int TokenRefreshBufferMinutes { get; set; } = 5;

        /// <summary>Maximum retry attempts for transient errors (429, 503). Default 3.</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>Base delay for exponential backoff in milliseconds. Default 500.</summary>
        public int RetryBaseDelayMs { get; set; } = 500;
    }

    /// <summary>
    /// GCP Cloud KMS key store using Application Default Credentials (ADC) pattern.
    /// Pure REST API implementation — no Google SDK dependencies.
    ///
    /// ADC resolution order:
    /// 1. GOOGLE_APPLICATION_CREDENTIALS env var -> service account JSON
    /// 2. gcloud CLI default credentials (~/.config/gcloud/application_default_credentials.json)
    /// 3. GCE metadata server (http://metadata.google.internal/computeMetadata/v1/)
    ///
    /// Operations:
    /// - Encrypt: POST /v1/{keyName}:encrypt
    /// - Decrypt: POST /v1/{keyName}:decrypt
    /// - Key URL: projects/{project}/locations/{location}/keyRings/{ring}/cryptoKeys/{key}
    /// </summary>
    public sealed class GcpKmsProvider : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private GcpKmsConfig _config = new();
        private HttpClient? _httpClient;
        private string? _cachedAccessToken;
        private DateTime _tokenExpiry = DateTime.MinValue;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);
        private AdcCredentialSource _credentialSource = AdcCredentialSource.None;
        private string? _serviceAccountJson;
        private string? _clientEmail;
        private string? _privateKeyPem;
        private string? _currentKeyId;

        private const string KmsBaseUrl = "https://cloudkms.googleapis.com";
        private const string TokenUrl = "https://oauth2.googleapis.com/token";
        private const string MetadataUrl = "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token";
        private const string KmsScope = "https://www.googleapis.com/auth/cloudkms";

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = true,
            SupportsHsm = true,  // GCP KMS supports HSM-backed keys
            SupportsExpiration = false,
            SupportsReplication = true,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,  // IAM-based
            SupportsAuditLogging = true,  // Cloud Audit Logs
            MaxKeySizeBytes = 64 * 1024,
            MinKeySizeBytes = 1,
            Metadata = new Dictionary<string, object>
            {
                ["StorageType"] = "CloudKMS",
                ["Provider"] = "GCP",
                ["AuthMethod"] = "ApplicationDefaultCredentials",
                ["ApiStyle"] = "REST",
                ["Encryption"] = "Google-managed or customer-managed CMEKs"
            }
        };

        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[]
        {
            "RSA-OAEP-256", "RSA-OAEP-3072-SHA256", "RSA-OAEP-4096-SHA256",
            "AES-256-GCM", "GOOGLE_SYMMETRIC_ENCRYPTION"
        };

        public bool SupportsHsmKeyGeneration => true;

        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            _httpClient?.Dispose();
            _httpClient = null;
            _cachedAccessToken = null;
            IncrementCounter("gcpkms.shutdown");
            return Task.CompletedTask;
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("gcpkms.init");
            LoadGcpConfig();
            _httpClient = new HttpClient { BaseAddress = new Uri(KmsBaseUrl) };
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Resolve ADC credentials
            await ResolveCredentialsAsync(cancellationToken).ConfigureAwait(false);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(token)) return false;

                // Test by listing key versions for the default key
                var keyName = BuildKeyName(_config.DefaultCryptoKey);
                using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/{keyName}/cryptoKeyVersions?pageSize=1");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var response = await _httpClient!.SendAsync(request, cancellationToken).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GcpKmsProvider.HealthCheckAsync] {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            EnsureReady();
            return Task.FromResult(_currentKeyId ?? _config.DefaultCryptoKey);
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            IncrementCounter("gcpkms.key.load");

            // For cloud KMS, "loading a key" means encrypting a well-known marker
            // and returning the ciphertext as the "key" — actual key material never leaves KMS.
            // In practice, callers use Encrypt/Decrypt directly.
            // For IKeyStore compatibility, we generate a local DEK and wrap it with KMS.
            var dek = new byte[32];
            RandomNumberGenerator.Fill(dek);

            // Wrap the DEK with KMS
            var wrappedDek = await EncryptWithKmsAsync(keyId, dek, CancellationToken.None).ConfigureAwait(false);

            // Store the mapping (wrapped DEK -> keyId) in local cache concept
            // Return the local DEK for use
            return dek;
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            IncrementCounter("gcpkms.key.save");
            _currentKeyId = keyId;
            // Keys in Cloud KMS are managed by GCP — we just track the key ID.
            // The actual key material lives within KMS and is never exported.
            await Task.CompletedTask;
        }

        /// <summary>
        /// Encrypts plaintext using GCP KMS.
        /// POST /v1/{keyName}:encrypt with JSON body { "plaintext": "base64..." }
        /// </summary>
        public async Task<byte[]> EncryptWithKmsAsync(string keyId, byte[] plaintext, CancellationToken ct = default)
        {
            IncrementCounter("gcpkms.encrypt");
            var keyName = BuildKeyName(keyId);
            var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);

            var requestBody = JsonSerializer.Serialize(new
            {
                plaintext = Convert.ToBase64String(plaintext)
            });

            return await ExecuteWithRetryAsync(async () =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/{keyName}:encrypt")
                {
                    Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var response = await _httpClient!.SendAsync(request, ct).ConfigureAwait(false);
                await EnsureSuccessAsync(response, "encrypt").ConfigureAwait(false);

                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var ciphertextBase64 = doc.RootElement.GetProperty("ciphertext").GetString()!;
                return Convert.FromBase64String(ciphertextBase64);
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Decrypts ciphertext using GCP KMS.
        /// POST /v1/{keyName}:decrypt with JSON body { "ciphertext": "base64..." }
        /// </summary>
        public async Task<byte[]> DecryptWithKmsAsync(string keyId, byte[] ciphertext, CancellationToken ct = default)
        {
            IncrementCounter("gcpkms.decrypt");
            var keyName = BuildKeyName(keyId);
            var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);

            var requestBody = JsonSerializer.Serialize(new
            {
                ciphertext = Convert.ToBase64String(ciphertext)
            });

            return await ExecuteWithRetryAsync(async () =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/{keyName}:decrypt")
                {
                    Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var response = await _httpClient!.SendAsync(request, ct).ConfigureAwait(false);
                await EnsureSuccessAsync(response, "decrypt").ConfigureAwait(false);

                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var plaintextBase64 = doc.RootElement.GetProperty("plaintext").GetString()!;
                return Convert.FromBase64String(plaintextBase64);
            }, ct).ConfigureAwait(false);
        }

        // IEnvelopeKeyStore implementation
        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            IncrementCounter("gcpkms.wrap");
            return await EncryptWithKmsAsync(kekId, dataKey, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            IncrementCounter("gcpkms.unwrap");
            return await DecryptWithKmsAsync(kekId, wrappedKey, CancellationToken.None).ConfigureAwait(false);
        }

        // ── ADC credential resolution ────────────────────────────

        private async Task ResolveCredentialsAsync(CancellationToken ct)
        {
            // 1. Check explicit config path or GOOGLE_APPLICATION_CREDENTIALS
            var saPath = _config.ServiceAccountJsonPath
                         ?? Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");

            if (!string.IsNullOrEmpty(saPath) && File.Exists(saPath))
            {
                _serviceAccountJson = await File.ReadAllTextAsync(saPath, ct).ConfigureAwait(false);
                ParseServiceAccountJson(_serviceAccountJson);
                _credentialSource = AdcCredentialSource.ServiceAccountJson;
                return;
            }

            // 2. Check gcloud CLI default credentials
            var gcloudCredPath = GetGcloudDefaultCredentialsPath();
            if (File.Exists(gcloudCredPath))
            {
                _serviceAccountJson = await File.ReadAllTextAsync(gcloudCredPath, ct).ConfigureAwait(false);
                ParseServiceAccountJson(_serviceAccountJson);
                _credentialSource = AdcCredentialSource.GcloudCli;
                return;
            }

            // 3. Check GCE metadata server
            if (await CheckMetadataServerAsync(ct).ConfigureAwait(false))
            {
                _credentialSource = AdcCredentialSource.MetadataServer;
                return;
            }

            // If no credentials found, log warning but don't fail — might be set later
            _credentialSource = AdcCredentialSource.None;
        }

        private void ParseServiceAccountJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("client_email", out var email))
                _clientEmail = email.GetString();

            if (root.TryGetProperty("private_key", out var key))
                _privateKeyPem = key.GetString();
        }

        private static string GetGcloudDefaultCredentialsPath()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".config", "gcloud", "application_default_credentials.json");
        }

        private async Task<bool> CheckMetadataServerAsync(CancellationToken ct)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var request = new HttpRequestMessage(HttpMethod.Get, MetadataUrl);
                request.Headers.Add("Metadata-Flavor", "Google");

                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GcpKmsProvider.CheckMetadataServerAsync] {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private async Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            // Check cached token
            if (_cachedAccessToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-_config.TokenRefreshBufferMinutes))
                return _cachedAccessToken;

            await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Double-check after acquiring lock
                if (_cachedAccessToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-_config.TokenRefreshBufferMinutes))
                    return _cachedAccessToken;

                switch (_credentialSource)
                {
                    case AdcCredentialSource.ServiceAccountJson:
                    case AdcCredentialSource.GcloudCli:
                        return await GetTokenFromServiceAccountAsync(ct).ConfigureAwait(false);

                    case AdcCredentialSource.MetadataServer:
                        return await GetTokenFromMetadataServerAsync(ct).ConfigureAwait(false);

                    default:
                        throw new InvalidOperationException(
                            "No GCP credentials available. Set GOOGLE_APPLICATION_CREDENTIALS, " +
                            "run 'gcloud auth application-default login', or run on GCE/GKE.");
                }
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        private async Task<string> GetTokenFromServiceAccountAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_clientEmail) || string.IsNullOrEmpty(_privateKeyPem))
                throw new InvalidOperationException("Service account JSON missing client_email or private_key.");

            // Build JWT for service account token exchange
            var now = DateTimeOffset.UtcNow;
            var header = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" })));

            var claims = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new
                {
                    iss = _clientEmail,
                    scope = KmsScope,
                    aud = TokenUrl,
                    iat = now.ToUnixTimeSeconds(),
                    exp = now.AddHours(1).ToUnixTimeSeconds()
                })));

            var signingInput = $"{Base64UrlEncode(header)}.{Base64UrlEncode(claims)}";

            // Sign with RSA private key
            using var rsa = RSA.Create();
            rsa.ImportFromPem(_privateKeyPem.AsSpan());
            var signatureBytes = rsa.SignData(
                Encoding.UTF8.GetBytes(signingInput),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            var jwt = $"{signingInput}.{Base64UrlEncode(Convert.ToBase64String(signatureBytes))}";

            // Exchange JWT for access token
            using var client = new HttpClient();
            var formContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
                new KeyValuePair<string, string>("assertion", jwt)
            });

            using var response = await client.PostAsync(TokenUrl, formContent, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            _cachedAccessToken = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);

            return _cachedAccessToken;
        }

        private async Task<string> GetTokenFromMetadataServerAsync(CancellationToken ct)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Get, MetadataUrl);
            request.Headers.Add("Metadata-Flavor", "Google");

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            _cachedAccessToken = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);

            return _cachedAccessToken;
        }

        // ── Helpers ──────────────────────────────────────────────

        private string BuildKeyName(string cryptoKey)
        {
            return $"projects/{_config.ProjectId}/locations/{_config.Location}" +
                   $"/keyRings/{_config.KeyRing}/cryptoKeys/{cryptoKey}";
        }

        private void LoadGcpConfig()
        {
            if (Configuration.TryGetValue("ProjectId", out var proj) && proj is string p)
                _config.ProjectId = p;
            if (Configuration.TryGetValue("Location", out var loc) && loc is string l)
                _config.Location = l;
            if (Configuration.TryGetValue("KeyRing", out var kr) && kr is string k)
                _config.KeyRing = k;
            if (Configuration.TryGetValue("DefaultCryptoKey", out var dk) && dk is string d)
                _config.DefaultCryptoKey = d;
            if (Configuration.TryGetValue("ServiceAccountJsonPath", out var sa) && sa is string s)
                _config.ServiceAccountJsonPath = s;
            if (Configuration.TryGetValue("MaxRetries", out var mr) && mr is int retries)
                _config.MaxRetries = retries;
            if (Configuration.TryGetValue("RetryBaseDelayMs", out var rb) && rb is int baseDelay)
                _config.RetryBaseDelayMs = baseDelay;
        }

        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
        {
            int attempt = 0;
            while (true)
            {
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (attempt < _config.MaxRetries && IsTransient(ex))
                {
                    attempt++;
                    IncrementCounter("gcpkms.retry");
                    var delay = _config.RetryBaseDelayMs * (int)Math.Pow(2, attempt - 1);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }

        private static bool IsTransient(HttpRequestException ex)
        {
            return ex.StatusCode == HttpStatusCode.TooManyRequests
                || ex.StatusCode == HttpStatusCode.ServiceUnavailable
                || ex.StatusCode == HttpStatusCode.GatewayTimeout;
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
        {
            if (response.IsSuccessStatusCode) return;

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException($"GCP KMS {operation} permission denied: {body}");

            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new KeyNotFoundException($"GCP KMS key not found during {operation}: {body}");

            throw new HttpRequestException(
                $"GCP KMS {operation} failed ({response.StatusCode}): {body}",
                null, response.StatusCode);
        }

        private static string Base64UrlEncode(string input)
        {
            return input.Replace("+", "-").Replace("/", "_").TrimEnd('=');
        }

        private void EnsureReady()
        {
            if (!IsInitialized)
                throw new InvalidOperationException("GcpKmsProvider has not been initialized.");
        }

        private enum AdcCredentialSource
        {
            None,
            ServiceAccountJson,
            GcloudCli,
            MetadataServer
        }
    }

    // ──────────────────────────────────────────────────────────────
    // AWS KMS Provider — SigV4-signed REST API
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Configuration for AWS KMS provider.
    /// </summary>
    public sealed class AwsKmsConfig
    {
        /// <summary>AWS region (e.g., "us-east-1").</summary>
        public string Region { get; set; } = "us-east-1";

        /// <summary>AWS access key ID. If empty, resolved from credential chain.</summary>
        public string? AccessKeyId { get; set; }

        /// <summary>AWS secret access key. If empty, resolved from credential chain.</summary>
        public string? SecretAccessKey { get; set; }

        /// <summary>AWS session token for temporary credentials.</summary>
        public string? SessionToken { get; set; }

        /// <summary>Default KMS key ARN or alias.</summary>
        public string DefaultKeyId { get; set; } = "alias/datawarehouse-default";

        /// <summary>Maximum retry attempts. Default 3.</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>Base delay for exponential backoff in milliseconds. Default 500.</summary>
        public int RetryBaseDelayMs { get; set; } = 500;

        /// <summary>Use IMDS v2 token for EC2 instance metadata. Default true.</summary>
        public bool UseImdsV2 { get; set; } = true;
    }

    /// <summary>
    /// AWS KMS key store using AWS Signature V4 REST API.
    /// Pure REST implementation — no AWS SDK dependencies.
    ///
    /// Credential chain:
    /// 1. Explicit AccessKeyId/SecretAccessKey in configuration
    /// 2. AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY env vars
    /// 3. ~/.aws/credentials file (default profile)
    /// 4. EC2 Instance Metadata Service v2 (IMDSv2)
    ///
    /// Operations via POST to kms.{region}.amazonaws.com with SigV4 signing.
    /// </summary>
    public sealed class AwsKmsProvider : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private AwsKmsConfig _config = new();
        private HttpClient? _httpClient;
        private string? _accessKeyId;
        private string? _secretAccessKey;
        private string? _sessionToken;
        private DateTime _credentialExpiry = DateTime.MaxValue;
        private readonly SemaphoreSlim _credentialLock = new(1, 1);
        private string? _currentKeyId;

        private const string KmsService = "kms";
        private const string ImdsTokenUrl = "http://169.254.169.254/latest/api/token";
        private const string ImdsCredentialUrl = "http://169.254.169.254/latest/meta-data/iam/security-credentials/";

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = true,
            SupportsHsm = true,  // AWS CloudHSM integration
            SupportsExpiration = true,
            SupportsReplication = true,  // Multi-region keys
            SupportsVersioning = false,
            SupportsPerKeyAcl = true,  // IAM key policies
            SupportsAuditLogging = true,  // CloudTrail
            MaxKeySizeBytes = 4096,
            MinKeySizeBytes = 1,
            Metadata = new Dictionary<string, object>
            {
                ["StorageType"] = "CloudKMS",
                ["Provider"] = "AWS",
                ["AuthMethod"] = "SignatureV4",
                ["ApiStyle"] = "REST",
                ["Encryption"] = "AWS-managed or customer-managed CMKs"
            }
        };

        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[]
        {
            "RSAES_OAEP_SHA_256", "RSAES_OAEP_SHA_1",
            "SYMMETRIC_DEFAULT", "SM2PKE"
        };

        public bool SupportsHsmKeyGeneration => true;

        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            _httpClient?.Dispose();
            _httpClient = null;
            _accessKeyId = null;
            _secretAccessKey = null;
            _sessionToken = null;
            IncrementCounter("awskms.shutdown");
            return Task.CompletedTask;
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("awskms.init");
            LoadAwsConfig();

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-amz-json-1.1"));

            await ResolveCredentialsAsync(cancellationToken).ConfigureAwait(false);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await EnsureCredentialsAsync(cancellationToken).ConfigureAwait(false);

                // Test by describing the default key
                var payload = JsonSerializer.Serialize(new { KeyId = _config.DefaultKeyId });
                var response = await SendKmsRequestAsync("TrentService.DescribeKey", payload, cancellationToken)
                    .ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AwsKmsProvider.HealthCheckAsync] {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            EnsureReady();
            return Task.FromResult(_currentKeyId ?? _config.DefaultKeyId);
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            IncrementCounter("awskms.key.load");

            // Generate a DEK via KMS GenerateDataKey
            var payload = JsonSerializer.Serialize(new
            {
                KeyId = ResolveKeyArn(keyId),
                KeySpec = "AES_256"
            });

            var response = await SendKmsRequestAsync("TrentService.GenerateDataKey", payload, CancellationToken.None)
                .ConfigureAwait(false);
            await EnsureAwsSuccessAsync(response, "GenerateDataKey").ConfigureAwait(false);

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var plaintextBase64 = doc.RootElement.GetProperty("Plaintext").GetString()!;
            return Convert.FromBase64String(plaintextBase64);
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            IncrementCounter("awskms.key.save");
            _currentKeyId = keyId;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Encrypts plaintext via AWS KMS Encrypt API.
        /// </summary>
        public async Task<byte[]> EncryptWithKmsAsync(string keyId, byte[] plaintext, CancellationToken ct = default)
        {
            IncrementCounter("awskms.encrypt");
            var payload = JsonSerializer.Serialize(new
            {
                KeyId = ResolveKeyArn(keyId),
                Plaintext = Convert.ToBase64String(plaintext)
            });

            var response = await SendKmsRequestAsync("TrentService.Encrypt", payload, ct).ConfigureAwait(false);
            await EnsureAwsSuccessAsync(response, "Encrypt").ConfigureAwait(false);

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var cipherBlob = doc.RootElement.GetProperty("CiphertextBlob").GetString()!;
            return Convert.FromBase64String(cipherBlob);
        }

        /// <summary>
        /// Decrypts ciphertext via AWS KMS Decrypt API.
        /// </summary>
        public async Task<byte[]> DecryptWithKmsAsync(string keyId, byte[] ciphertext, CancellationToken ct = default)
        {
            IncrementCounter("awskms.decrypt");
            var payload = JsonSerializer.Serialize(new
            {
                KeyId = ResolveKeyArn(keyId),
                CiphertextBlob = Convert.ToBase64String(ciphertext)
            });

            var response = await SendKmsRequestAsync("TrentService.Decrypt", payload, ct).ConfigureAwait(false);
            await EnsureAwsSuccessAsync(response, "Decrypt").ConfigureAwait(false);

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var plaintextBase64 = doc.RootElement.GetProperty("Plaintext").GetString()!;
            return Convert.FromBase64String(plaintextBase64);
        }

        // IEnvelopeKeyStore
        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            IncrementCounter("awskms.wrap");
            return await EncryptWithKmsAsync(kekId, dataKey, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            IncrementCounter("awskms.unwrap");
            return await DecryptWithKmsAsync(kekId, wrappedKey, CancellationToken.None).ConfigureAwait(false);
        }

        // ── AWS credential resolution ────────────────────────────

        private async Task ResolveCredentialsAsync(CancellationToken ct)
        {
            // 1. Explicit config
            if (!string.IsNullOrEmpty(_config.AccessKeyId) && !string.IsNullOrEmpty(_config.SecretAccessKey))
            {
                _accessKeyId = _config.AccessKeyId;
                _secretAccessKey = _config.SecretAccessKey;
                _sessionToken = _config.SessionToken;
                return;
            }

            // 2. Environment variables
            var envAccessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
            var envSecretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
            if (!string.IsNullOrEmpty(envAccessKey) && !string.IsNullOrEmpty(envSecretKey))
            {
                _accessKeyId = envAccessKey;
                _secretAccessKey = envSecretKey;
                _sessionToken = Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN");
                return;
            }

            // 3. AWS credentials file
            if (TryLoadCredentialsFile())
                return;

            // 4. EC2 IMDS v2
            await TryLoadImdsCredentialsAsync(ct).ConfigureAwait(false);
        }

        private bool TryLoadCredentialsFile()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var credFile = Path.Combine(home, ".aws", "credentials");

            if (!File.Exists(credFile)) return false;

            try
            {
                var lines = File.ReadAllLines(credFile);
                var inDefaultProfile = false;

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed == "[default]") { inDefaultProfile = true; continue; }
                    if (trimmed.StartsWith("[")) { inDefaultProfile = false; continue; }
                    if (!inDefaultProfile) continue;

                    var parts = trimmed.Split('=', 2);
                    if (parts.Length != 2) continue;

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    switch (key)
                    {
                        case "aws_access_key_id": _accessKeyId = value; break;
                        case "aws_secret_access_key": _secretAccessKey = value; break;
                        case "aws_session_token": _sessionToken = value; break;
                    }
                }

                return !string.IsNullOrEmpty(_accessKeyId) && !string.IsNullOrEmpty(_secretAccessKey);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AwsKmsProvider.TryLoadCredentialsFile] {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private async Task TryLoadImdsCredentialsAsync(CancellationToken ct)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

                // IMDSv2: get token first
                string? imdsToken = null;
                if (_config.UseImdsV2)
                {
                    using var tokenReq = new HttpRequestMessage(HttpMethod.Put, ImdsTokenUrl);
                    tokenReq.Headers.Add("X-aws-ec2-metadata-token-ttl-seconds", "21600");
                    var tokenResp = await client.SendAsync(tokenReq, ct).ConfigureAwait(false);
                    if (tokenResp.IsSuccessStatusCode)
                        imdsToken = await tokenResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }

                // Get IAM role name
                using var roleReq = new HttpRequestMessage(HttpMethod.Get, ImdsCredentialUrl);
                if (imdsToken != null)
                    roleReq.Headers.Add("X-aws-ec2-metadata-token", imdsToken);

                var roleResp = await client.SendAsync(roleReq, ct).ConfigureAwait(false);
                if (!roleResp.IsSuccessStatusCode) return;

                var roleName = (await roleResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();

                // Get credentials for role
                using var credReq = new HttpRequestMessage(HttpMethod.Get, ImdsCredentialUrl + roleName);
                if (imdsToken != null)
                    credReq.Headers.Add("X-aws-ec2-metadata-token", imdsToken);

                var credResp = await client.SendAsync(credReq, ct).ConfigureAwait(false);
                if (!credResp.IsSuccessStatusCode) return;

                var json = await credResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                _accessKeyId = doc.RootElement.GetProperty("AccessKeyId").GetString();
                _secretAccessKey = doc.RootElement.GetProperty("SecretAccessKey").GetString();
                _sessionToken = doc.RootElement.GetProperty("Token").GetString();

                if (doc.RootElement.TryGetProperty("Expiration", out var exp))
                    _credentialExpiry = DateTime.Parse(exp.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AwsKmsProvider.TryLoadImdsCredentialsAsync] {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task EnsureCredentialsAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_accessKeyId) && DateTime.UtcNow < _credentialExpiry.AddMinutes(-5))
                return;

            await _credentialLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!string.IsNullOrEmpty(_accessKeyId) && DateTime.UtcNow < _credentialExpiry.AddMinutes(-5))
                    return;

                await ResolveCredentialsAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _credentialLock.Release();
            }
        }

        // ── AWS SigV4 request signing ────────────────────────────

        private async Task<HttpResponseMessage> SendKmsRequestAsync(string target, string payload, CancellationToken ct)
        {
            await EnsureCredentialsAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(_accessKeyId) || string.IsNullOrEmpty(_secretAccessKey))
                throw new InvalidOperationException(
                    "No AWS credentials available. Set AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY, " +
                    "configure ~/.aws/credentials, or run on an EC2 instance with an IAM role.");

            var endpoint = $"https://kms.{_config.Region}.amazonaws.com";
            var now = DateTime.UtcNow;
            var dateStamp = now.ToString("yyyyMMdd");
            var amzDate = now.ToString("yyyyMMddTHHmmssZ");

            var payloadHash = HashSha256(payload);

            // Canonical request
            var canonicalHeaders = $"content-type:application/x-amz-json-1.1\n" +
                                   $"host:kms.{_config.Region}.amazonaws.com\n" +
                                   $"x-amz-date:{amzDate}\n" +
                                   $"x-amz-target:{target}\n";

            var signedHeaders = "content-type;host;x-amz-date;x-amz-target";

            if (!string.IsNullOrEmpty(_sessionToken))
            {
                canonicalHeaders += $"x-amz-security-token:{_sessionToken}\n";
                signedHeaders += ";x-amz-security-token";
            }

            var canonicalRequest = $"POST\n/\n\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

            // String to sign
            var credentialScope = $"{dateStamp}/{_config.Region}/{KmsService}/aws4_request";
            var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{HashSha256(canonicalRequest)}";

            // Signing key
            var signingKey = GetSignatureKey(_secretAccessKey!, dateStamp, _config.Region, KmsService);
            var signature = HmacSha256Hex(signingKey, stringToSign);

            var authorization = $"AWS4-HMAC-SHA256 Credential={_accessKeyId}/{credentialScope}, " +
                               $"SignedHeaders={signedHeaders}, Signature={signature}";

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/x-amz-json-1.1")
            };

            request.Headers.Add("X-Amz-Date", amzDate);
            request.Headers.Add("X-Amz-Target", target);
            request.Headers.Add("Authorization", authorization);

            if (!string.IsNullOrEmpty(_sessionToken))
                request.Headers.Add("X-Amz-Security-Token", _sessionToken);

            return await _httpClient!.SendAsync(request, ct).ConfigureAwait(false);
        }

        // ── SigV4 crypto helpers ─────────────────────────────────

        private static string HashSha256(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static byte[] HmacSha256(byte[] key, string data)
        {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        }

        private static string HmacSha256Hex(byte[] key, string data)
        {
            return Convert.ToHexString(HmacSha256(key, data)).ToLowerInvariant();
        }

        private static byte[] GetSignatureKey(string key, string dateStamp, string region, string service)
        {
            var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + key), dateStamp);
            var kRegion = HmacSha256(kDate, region);
            var kService = HmacSha256(kRegion, service);
            return HmacSha256(kService, "aws4_request");
        }

        private static async Task EnsureAwsSuccessAsync(HttpResponseMessage response, string operation)
        {
            if (response.IsSuccessStatusCode) return;

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Unauthorized)
                throw new UnauthorizedAccessException($"AWS KMS {operation} access denied: {body}");

            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new KeyNotFoundException($"AWS KMS key not found during {operation}: {body}");

            throw new HttpRequestException(
                $"AWS KMS {operation} failed ({response.StatusCode}): {body}",
                null, response.StatusCode);
        }

        // ── Config helpers ───────────────────────────────────────

        private void LoadAwsConfig()
        {
            if (Configuration.TryGetValue("Region", out var region) && region is string r)
                _config.Region = r;
            if (Configuration.TryGetValue("AccessKeyId", out var aki) && aki is string a)
                _config.AccessKeyId = a;
            if (Configuration.TryGetValue("SecretAccessKey", out var sak) && sak is string s)
                _config.SecretAccessKey = s;
            if (Configuration.TryGetValue("SessionToken", out var st) && st is string t)
                _config.SessionToken = t;
            if (Configuration.TryGetValue("DefaultKeyId", out var dk) && dk is string d)
                _config.DefaultKeyId = d;
            if (Configuration.TryGetValue("MaxRetries", out var mr) && mr is int retries)
                _config.MaxRetries = retries;
            if (Configuration.TryGetValue("UseImdsV2", out var imds) && imds is bool useImds)
                _config.UseImdsV2 = useImds;
        }

        private string ResolveKeyArn(string keyId)
        {
            // If already a full ARN, use as-is
            if (keyId.StartsWith("arn:aws:kms:") || keyId.StartsWith("alias/"))
                return keyId;

            // Otherwise treat as key ID / alias
            return keyId;
        }

        private void EnsureReady()
        {
            if (!IsInitialized)
                throw new InvalidOperationException("AwsKmsProvider has not been initialized.");
        }
    }
}
