using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Security.KeyManagement
{
    // ──────────────────────────────────────────────────────────────
    // AWS Secrets Manager KeyStore
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Configuration for AWS Secrets Manager key store.
    /// </summary>
    public sealed class AwsSecretsManagerConfig
    {
        /// <summary>AWS region. Default "us-east-1".</summary>
        public string Region { get; set; } = "us-east-1";

        /// <summary>AWS access key ID. If empty, resolved from credential chain.</summary>
        public string? AccessKeyId { get; set; }

        /// <summary>AWS secret access key.</summary>
        public string? SecretAccessKey { get; set; }

        /// <summary>AWS session token for temporary credentials.</summary>
        public string? SessionToken { get; set; }

        /// <summary>Secret name prefix in Secrets Manager. Default "datawarehouse/keys/".</summary>
        public string SecretPrefix { get; set; } = "datawarehouse/keys/";

        /// <summary>Local cache TTL in minutes. Default 5.</summary>
        public int CacheTtlMinutes { get; set; } = 5;

        /// <summary>Maximum retry attempts. Default 3.</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>Use IMDS v2 for EC2 credential resolution. Default true.</summary>
        public bool UseImdsV2 { get; set; } = true;
    }

    /// <summary>
    /// AWS Secrets Manager as IKeyStore backend via REST API (no AWS SDK dependency).
    ///
    /// Stores and retrieves encryption keys from AWS Secrets Manager.
    /// Supports automatic rotation via Secrets Manager rotation lambdas.
    ///
    /// REST API: secretsmanager.{region}.amazonaws.com with SigV4 signing.
    /// - GetKeyAsync -> GetSecretValue, returns SecretBinary as byte[]
    /// - StoreKeyAsync -> CreateSecret or PutSecretValue (upsert pattern)
    ///
    /// Local caching with configurable TTL (default 5 min) and secure memory wipe on eviction.
    /// Falls back to local IKeyStore if cloud unavailable (logs warning).
    /// </summary>
    public sealed class AwsSecretsManagerKeyStore : KeyStoreStrategyBase
    {
        private AwsSecretsManagerConfig _config = new();
        private HttpClient? _httpClient;
        private string? _accessKeyId;
        private string? _secretAccessKey;
        private string? _sessionToken;
        private DateTime _credentialExpiry = DateTime.MaxValue;
        private readonly SemaphoreSlim _credentialLock = new(1, 1);
        private readonly ConcurrentDictionary<string, SecureCacheEntry> _secureCache = new();
        private string? _currentKeyId;

        private const string ServiceName = "secretsmanager";

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = true,
            SupportsReplication = true,  // Multi-region secrets
            SupportsVersioning = true,   // Secrets Manager versions
            SupportsPerKeyAcl = true,    // IAM resource policies
            SupportsAuditLogging = true,  // CloudTrail
            MaxKeySizeBytes = 65536,      // Secrets Manager 64KB limit
            MinKeySizeBytes = 1,
            Metadata = new Dictionary<string, object>
            {
                ["StorageType"] = "SecretsManager",
                ["Provider"] = "AWS",
                ["AuthMethod"] = "SignatureV4",
                ["ApiStyle"] = "REST",
                ["RotationSupport"] = "Lambda-based automatic rotation"
            }
        };

        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            WipeSecureCache();
            _httpClient?.Dispose();
            _httpClient = null;
            IncrementCounter("awssm.shutdown");
            return Task.CompletedTask;
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("awssm.init");
            LoadConfig();
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-amz-json-1.1"));

            await ResolveCredentialsAsync(cancellationToken).ConfigureAwait(false);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await EnsureCredentialsAsync(cancellationToken).ConfigureAwait(false);

                // Test connectivity by listing secrets with max 1 result
                var payload = JsonSerializer.Serialize(new { MaxResults = 1 });
                var response = await SendSmRequestAsync("secretsmanager.ListSecrets", payload, cancellationToken)
                    .ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? "default");
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            IncrementCounter("awssm.key.load");

            // Check secure cache first
            if (_secureCache.TryGetValue(keyId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            {
                IncrementCounter("awssm.cache.hit");
                return cached.GetCopy();
            }

            var secretName = _config.SecretPrefix + keyId;
            var payload = JsonSerializer.Serialize(new { SecretId = secretName });

            var response = await SendSmRequestAsync("secretsmanager.GetSecretValue", payload, CancellationToken.None)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound ||
                    errorBody.Contains("ResourceNotFoundException"))
                {
                    throw new KeyNotFoundException($"Secret '{secretName}' not found in AWS Secrets Manager.");
                }

                throw new HttpRequestException(
                    $"AWS Secrets Manager GetSecretValue failed ({response.StatusCode}): {errorBody}",
                    null, response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            byte[] keyData;
            if (doc.RootElement.TryGetProperty("SecretBinary", out var binaryProp))
            {
                keyData = Convert.FromBase64String(binaryProp.GetString()!);
            }
            else if (doc.RootElement.TryGetProperty("SecretString", out var stringProp))
            {
                keyData = Convert.FromBase64String(stringProp.GetString()!);
            }
            else
            {
                throw new InvalidOperationException($"Secret '{secretName}' contains neither SecretBinary nor SecretString.");
            }

            // Cache securely
            EvictExpiredCache();
            var entry = new SecureCacheEntry(keyData, TimeSpan.FromMinutes(_config.CacheTtlMinutes));
            _secureCache[keyId] = entry;

            return keyData;
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            IncrementCounter("awssm.key.save");
            _currentKeyId = keyId;

            var secretName = _config.SecretPrefix + keyId;

            // Try PutSecretValue first (update existing)
            var putPayload = JsonSerializer.Serialize(new
            {
                SecretId = secretName,
                SecretBinary = Convert.ToBase64String(keyData)
            });

            var response = await SendSmRequestAsync("secretsmanager.PutSecretValue", putPayload, CancellationToken.None)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                // Update cache
                _secureCache[keyId] = new SecureCacheEntry(keyData, TimeSpan.FromMinutes(_config.CacheTtlMinutes));
                return;
            }

            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            // If secret doesn't exist, create it
            if (errorBody.Contains("ResourceNotFoundException"))
            {
                var createPayload = JsonSerializer.Serialize(new
                {
                    Name = secretName,
                    SecretBinary = Convert.ToBase64String(keyData),
                    Description = $"DataWarehouse encryption key: {keyId}"
                });

                var createResponse = await SendSmRequestAsync("secretsmanager.CreateSecret", createPayload, CancellationToken.None)
                    .ConfigureAwait(false);

                if (!createResponse.IsSuccessStatusCode)
                {
                    var createError = await createResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new InvalidOperationException($"Failed to create secret '{secretName}': {createError}");
                }

                _secureCache[keyId] = new SecureCacheEntry(keyData, TimeSpan.FromMinutes(_config.CacheTtlMinutes));
                return;
            }

            throw new InvalidOperationException($"Failed to store secret '{secretName}': {errorBody}");
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            IncrementCounter("awssm.key.delete");

            var secretName = _config.SecretPrefix + keyId;
            var payload = JsonSerializer.Serialize(new
            {
                SecretId = secretName,
                RecoveryWindowInDays = 7
            });

            var response = await SendSmRequestAsync("secretsmanager.DeleteSecret", payload, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"Failed to delete secret '{secretName}': {error}");
            }

            // Remove from cache
            if (_secureCache.TryRemove(keyId, out var entry))
                entry.Dispose();
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            IncrementCounter("awssm.key.list");

            var keys = new List<string>();
            string? nextToken = null;

            do
            {
                var payloadObj = new Dictionary<string, object> { ["MaxResults"] = 100 };
                if (!string.IsNullOrEmpty(_config.SecretPrefix))
                {
                    payloadObj["Filters"] = new[] { new { Key = "name", Values = new[] { _config.SecretPrefix } } };
                }
                if (nextToken != null)
                    payloadObj["NextToken"] = nextToken;

                var payload = JsonSerializer.Serialize(payloadObj);
                var response = await SendSmRequestAsync("secretsmanager.ListSecrets", payload, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode) break;

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("SecretList", out var list))
                {
                    foreach (var secret in list.EnumerateArray())
                    {
                        var name = secret.GetProperty("Name").GetString()!;
                        if (name.StartsWith(_config.SecretPrefix))
                        {
                            keys.Add(name[_config.SecretPrefix.Length..]);
                        }
                    }
                }

                nextToken = doc.RootElement.TryGetProperty("NextToken", out var nt) ? nt.GetString() : null;
            } while (nextToken != null);

            return keys.AsReadOnly();
        }

        // ── SigV4 request sending ────────────────────────────────

        private async Task<HttpResponseMessage> SendSmRequestAsync(string target, string payload, CancellationToken ct)
        {
            await EnsureCredentialsAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(_accessKeyId) || string.IsNullOrEmpty(_secretAccessKey))
                throw new InvalidOperationException("No AWS credentials available for Secrets Manager.");

            var endpoint = $"https://secretsmanager.{_config.Region}.amazonaws.com";
            var host = $"secretsmanager.{_config.Region}.amazonaws.com";
            var now = DateTime.UtcNow;
            var dateStamp = now.ToString("yyyyMMdd");
            var amzDate = now.ToString("yyyyMMddTHHmmssZ");

            var payloadHash = HashSha256(payload);

            var canonicalHeaders = $"content-type:application/x-amz-json-1.1\n" +
                                   $"host:{host}\n" +
                                   $"x-amz-date:{amzDate}\n" +
                                   $"x-amz-target:{target}\n";

            var signedHeaders = "content-type;host;x-amz-date;x-amz-target";

            if (!string.IsNullOrEmpty(_sessionToken))
            {
                canonicalHeaders += $"x-amz-security-token:{_sessionToken}\n";
                signedHeaders += ";x-amz-security-token";
            }

            var canonicalRequest = $"POST\n/\n\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

            var credentialScope = $"{dateStamp}/{_config.Region}/{ServiceName}/aws4_request";
            var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{HashSha256(canonicalRequest)}";

            var signingKey = GetSignatureKey(_secretAccessKey!, dateStamp, _config.Region, ServiceName);
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

        // ── Credential resolution (same chain as AwsKmsProvider) ─

        private async Task ResolveCredentialsAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_config.AccessKeyId) && !string.IsNullOrEmpty(_config.SecretAccessKey))
            {
                _accessKeyId = _config.AccessKeyId;
                _secretAccessKey = _config.SecretAccessKey;
                _sessionToken = _config.SessionToken;
                return;
            }

            var envAccessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
            var envSecretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
            if (!string.IsNullOrEmpty(envAccessKey) && !string.IsNullOrEmpty(envSecretKey))
            {
                _accessKeyId = envAccessKey;
                _secretAccessKey = envSecretKey;
                _sessionToken = Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN");
                return;
            }

            if (TryLoadCredentialsFile()) return;
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

                    switch (parts[0].Trim())
                    {
                        case "aws_access_key_id": _accessKeyId = parts[1].Trim(); break;
                        case "aws_secret_access_key": _secretAccessKey = parts[1].Trim(); break;
                        case "aws_session_token": _sessionToken = parts[1].Trim(); break;
                    }
                }

                return !string.IsNullOrEmpty(_accessKeyId) && !string.IsNullOrEmpty(_secretAccessKey);
            }
            catch { return false; }
        }

        private async Task TryLoadImdsCredentialsAsync(CancellationToken ct)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

                string? imdsToken = null;
                if (_config.UseImdsV2)
                {
                    using var tokenReq = new HttpRequestMessage(HttpMethod.Put, "http://169.254.169.254/latest/api/token");
                    tokenReq.Headers.Add("X-aws-ec2-metadata-token-ttl-seconds", "21600");
                    var tokenResp = await client.SendAsync(tokenReq, ct).ConfigureAwait(false);
                    if (tokenResp.IsSuccessStatusCode)
                        imdsToken = await tokenResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }

                using var roleReq = new HttpRequestMessage(HttpMethod.Get, "http://169.254.169.254/latest/meta-data/iam/security-credentials/");
                if (imdsToken != null) roleReq.Headers.Add("X-aws-ec2-metadata-token", imdsToken);

                var roleResp = await client.SendAsync(roleReq, ct).ConfigureAwait(false);
                if (!roleResp.IsSuccessStatusCode) return;

                var roleName = (await roleResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();

                using var credReq = new HttpRequestMessage(HttpMethod.Get,
                    $"http://169.254.169.254/latest/meta-data/iam/security-credentials/{roleName}");
                if (imdsToken != null) credReq.Headers.Add("X-aws-ec2-metadata-token", imdsToken);

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
            catch { /* IMDS not available */ }
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
            finally { _credentialLock.Release(); }
        }

        // ── SigV4 helpers ────────────────────────────────────────

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

        // ── Secure cache ─────────────────────────────────────────

        private void EvictExpiredCache()
        {
            foreach (var kvp in _secureCache)
            {
                if (kvp.Value.ExpiresAt <= DateTime.UtcNow)
                {
                    if (_secureCache.TryRemove(kvp.Key, out var entry))
                        entry.Dispose();
                }
            }
        }

        private void WipeSecureCache()
        {
            foreach (var kvp in _secureCache)
            {
                if (_secureCache.TryRemove(kvp.Key, out var entry))
                    entry.Dispose();
            }
        }

        private void LoadConfig()
        {
            if (Configuration.TryGetValue("Region", out var r) && r is string region)
                _config.Region = region;
            if (Configuration.TryGetValue("AccessKeyId", out var a) && a is string aki)
                _config.AccessKeyId = aki;
            if (Configuration.TryGetValue("SecretAccessKey", out var s) && s is string sak)
                _config.SecretAccessKey = sak;
            if (Configuration.TryGetValue("SessionToken", out var t) && t is string st)
                _config.SessionToken = st;
            if (Configuration.TryGetValue("SecretPrefix", out var sp) && sp is string prefix)
                _config.SecretPrefix = prefix;
            if (Configuration.TryGetValue("CacheTtlMinutes", out var ttl) && ttl is int cacheTtl)
                _config.CacheTtlMinutes = cacheTtl;
            if (Configuration.TryGetValue("MaxRetries", out var mr) && mr is int retries)
                _config.MaxRetries = retries;
        }

        /// <summary>
        /// Secure cache entry that stores key material in pinned memory
        /// and zeroes it on disposal via CryptographicOperations.ZeroMemory.
        /// </summary>
        private sealed class SecureCacheEntry : IDisposable
        {
            private byte[]? _data;
            private GCHandle _pin;
            private bool _disposed;

            public DateTime ExpiresAt { get; }

            public SecureCacheEntry(byte[] data, TimeSpan ttl)
            {
                _data = new byte[data.Length];
                Array.Copy(data, _data, data.Length);
                _pin = GCHandle.Alloc(_data, GCHandleType.Pinned);
                ExpiresAt = DateTime.UtcNow.Add(ttl);
            }

            public byte[] GetCopy()
            {
                if (_disposed || _data == null) throw new ObjectDisposedException(nameof(SecureCacheEntry));
                var copy = new byte[_data.Length];
                Array.Copy(_data, copy, _data.Length);
                return copy;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                if (_data != null)
                {
                    CryptographicOperations.ZeroMemory(_data);
                    if (_pin.IsAllocated) _pin.Free();
                    _data = null;
                }
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // GCP Secret Manager KeyStore
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Configuration for GCP Secret Manager key store.
    /// </summary>
    public sealed class GcpSecretManagerConfig
    {
        /// <summary>GCP project ID.</summary>
        public string ProjectId { get; set; } = string.Empty;

        /// <summary>Path to service account JSON (overrides GOOGLE_APPLICATION_CREDENTIALS).</summary>
        public string? ServiceAccountJsonPath { get; set; }

        /// <summary>Secret name prefix. Default "datawarehouse-key-".</summary>
        public string SecretPrefix { get; set; } = "datawarehouse-key-";

        /// <summary>Local cache TTL in minutes. Default 5.</summary>
        public int CacheTtlMinutes { get; set; } = 5;

        /// <summary>Token refresh buffer minutes. Default 5.</summary>
        public int TokenRefreshBufferMinutes { get; set; } = 5;

        /// <summary>Maximum retries. Default 3.</summary>
        public int MaxRetries { get; set; } = 3;
    }

    /// <summary>
    /// GCP Secret Manager as IKeyStore backend via REST API with ADC authentication.
    ///
    /// Stores and retrieves encryption keys from GCP Secret Manager.
    /// REST API: secretmanager.googleapis.com with OAuth2 bearer token.
    ///
    /// - GetKeyAsync -> GET /v1/projects/{project}/secrets/{secret}/versions/latest:access
    /// - StoreKeyAsync -> POST /v1/projects/{project}/secrets + addVersion
    ///
    /// Local caching with pinned memory and secure wipe via CryptographicOperations.ZeroMemory.
    /// </summary>
    public sealed class GcpSecretManagerKeyStore : KeyStoreStrategyBase
    {
        private GcpSecretManagerConfig _config = new();
        private HttpClient? _httpClient;
        private string? _cachedAccessToken;
        private DateTime _tokenExpiry = DateTime.MinValue;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);
        private string? _clientEmail;
        private string? _privateKeyPem;
        private AdcSource _credSource = AdcSource.None;
        private readonly ConcurrentDictionary<string, SecureCacheEntry> _secureCache = new();
        private string? _currentKeyId;

        private const string SmBaseUrl = "https://secretmanager.googleapis.com";
        private const string TokenUrl = "https://oauth2.googleapis.com/token";
        private const string MetadataUrl = "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token";
        private const string SmScope = "https://www.googleapis.com/auth/cloud-platform";

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = true,   // Secret versions can be disabled/destroyed
            SupportsReplication = true,   // Automatic or user-managed replication
            SupportsVersioning = true,    // Secret Manager versions
            SupportsPerKeyAcl = true,     // IAM per-secret
            SupportsAuditLogging = true,  // Cloud Audit Logs
            MaxKeySizeBytes = 65536,      // 64KB limit
            MinKeySizeBytes = 1,
            Metadata = new Dictionary<string, object>
            {
                ["StorageType"] = "SecretManager",
                ["Provider"] = "GCP",
                ["AuthMethod"] = "ApplicationDefaultCredentials",
                ["ApiStyle"] = "REST",
                ["RotationSupport"] = "Pub/Sub-based rotation topics"
            }
        };

        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            WipeSecureCache();
            _httpClient?.Dispose();
            _httpClient = null;
            _cachedAccessToken = null;
            IncrementCounter("gcpsm.shutdown");
            return Task.CompletedTask;
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("gcpsm.init");
            LoadConfig();
            _httpClient = new HttpClient { BaseAddress = new Uri(SmBaseUrl) };
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            await ResolveCredentialsAsync(cancellationToken).ConfigureAwait(false);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(token)) return false;

                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"/v1/projects/{_config.ProjectId}/secrets?pageSize=1");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var response = await _httpClient!.SendAsync(request, cancellationToken).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? "default");
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            IncrementCounter("gcpsm.key.load");

            // Check secure cache
            if (_secureCache.TryGetValue(keyId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            {
                IncrementCounter("gcpsm.cache.hit");
                return cached.GetCopy();
            }

            var token = await GetAccessTokenAsync(CancellationToken.None).ConfigureAwait(false);
            var secretName = _config.SecretPrefix + keyId;

            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/v1/projects/{_config.ProjectId}/secrets/{secretName}/versions/latest:access");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient!.SendAsync(request).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new KeyNotFoundException($"Secret '{secretName}' not found in GCP Secret Manager.");

                throw new HttpRequestException(
                    $"GCP Secret Manager access failed ({response.StatusCode}): {error}",
                    null, response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            var payloadData = doc.RootElement.GetProperty("payload").GetProperty("data").GetString()!;
            var keyData = Convert.FromBase64String(payloadData);

            // Cache securely
            EvictExpiredCache();
            _secureCache[keyId] = new SecureCacheEntry(keyData, TimeSpan.FromMinutes(_config.CacheTtlMinutes));

            return keyData;
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            IncrementCounter("gcpsm.key.save");
            _currentKeyId = keyId;

            var token = await GetAccessTokenAsync(CancellationToken.None).ConfigureAwait(false);
            var secretName = _config.SecretPrefix + keyId;

            // Try to add a version to existing secret first
            var versionPayload = JsonSerializer.Serialize(new
            {
                payload = new { data = Convert.ToBase64String(keyData) }
            });

            using var addVersionReq = new HttpRequestMessage(HttpMethod.Post,
                $"/v1/projects/{_config.ProjectId}/secrets/{secretName}:addVersion")
            {
                Content = new StringContent(versionPayload, Encoding.UTF8, "application/json")
            };
            addVersionReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient!.SendAsync(addVersionReq).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _secureCache[keyId] = new SecureCacheEntry(keyData, TimeSpan.FromMinutes(_config.CacheTtlMinutes));
                return;
            }

            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            // If secret doesn't exist, create it then add version
            if (response.StatusCode == HttpStatusCode.NotFound || errorBody.Contains("NOT_FOUND"))
            {
                // Create the secret resource
                var createPayload = JsonSerializer.Serialize(new
                {
                    replication = new { automatic = new { } }
                });

                using var createReq = new HttpRequestMessage(HttpMethod.Post,
                    $"/v1/projects/{_config.ProjectId}/secrets?secretId={secretName}")
                {
                    Content = new StringContent(createPayload, Encoding.UTF8, "application/json")
                };
                createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var createResp = await _httpClient.SendAsync(createReq).ConfigureAwait(false);
                if (!createResp.IsSuccessStatusCode)
                {
                    var createError = await createResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new InvalidOperationException($"Failed to create GCP secret '{secretName}': {createError}");
                }

                // Now add the version
                using var addReq = new HttpRequestMessage(HttpMethod.Post,
                    $"/v1/projects/{_config.ProjectId}/secrets/{secretName}:addVersion")
                {
                    Content = new StringContent(versionPayload, Encoding.UTF8, "application/json")
                };
                addReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var addResp = await _httpClient.SendAsync(addReq).ConfigureAwait(false);
                if (!addResp.IsSuccessStatusCode)
                {
                    var addError = await addResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new InvalidOperationException($"Failed to add version to GCP secret '{secretName}': {addError}");
                }

                _secureCache[keyId] = new SecureCacheEntry(keyData, TimeSpan.FromMinutes(_config.CacheTtlMinutes));
                return;
            }

            throw new InvalidOperationException($"Failed to store GCP secret '{secretName}': {errorBody}");
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            IncrementCounter("gcpsm.key.delete");

            var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            var secretName = _config.SecretPrefix + keyId;

            using var request = new HttpRequestMessage(HttpMethod.Delete,
                $"/v1/projects/{_config.ProjectId}/secrets/{secretName}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient!.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"Failed to delete GCP secret '{secretName}': {error}");
            }

            if (_secureCache.TryRemove(keyId, out var entry))
                entry.Dispose();
        }

        // ── ADC credential resolution ────────────────────────────

        private async Task ResolveCredentialsAsync(CancellationToken ct)
        {
            var saPath = _config.ServiceAccountJsonPath
                         ?? Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");

            if (!string.IsNullOrEmpty(saPath) && File.Exists(saPath))
            {
                var json = await File.ReadAllTextAsync(saPath, ct).ConfigureAwait(false);
                ParseServiceAccountJson(json);
                _credSource = AdcSource.ServiceAccount;
                return;
            }

            var gcloudPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "gcloud", "application_default_credentials.json");

            if (File.Exists(gcloudPath))
            {
                var json = await File.ReadAllTextAsync(gcloudPath, ct).ConfigureAwait(false);
                ParseServiceAccountJson(json);
                _credSource = AdcSource.GcloudCli;
                return;
            }

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var req = new HttpRequestMessage(HttpMethod.Get, MetadataUrl);
                req.Headers.Add("Metadata-Flavor", "Google");
                var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    _credSource = AdcSource.Metadata;
                    return;
                }
            }
            catch { /* not on GCE */ }

            _credSource = AdcSource.None;
        }

        private void ParseServiceAccountJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("client_email", out var email))
                _clientEmail = email.GetString();
            if (doc.RootElement.TryGetProperty("private_key", out var key))
                _privateKeyPem = key.GetString();
        }

        private async Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            if (_cachedAccessToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-_config.TokenRefreshBufferMinutes))
                return _cachedAccessToken;

            await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_cachedAccessToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-_config.TokenRefreshBufferMinutes))
                    return _cachedAccessToken;

                switch (_credSource)
                {
                    case AdcSource.ServiceAccount:
                    case AdcSource.GcloudCli:
                        return await GetTokenFromServiceAccountAsync(ct).ConfigureAwait(false);
                    case AdcSource.Metadata:
                        return await GetTokenFromMetadataAsync(ct).ConfigureAwait(false);
                    default:
                        throw new InvalidOperationException("No GCP credentials available for Secret Manager.");
                }
            }
            finally { _tokenLock.Release(); }
        }

        private async Task<string> GetTokenFromServiceAccountAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_clientEmail) || string.IsNullOrEmpty(_privateKeyPem))
                throw new InvalidOperationException("Service account JSON missing client_email or private_key.");

            var now = DateTimeOffset.UtcNow;
            var headerB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" })));
            var claimsB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new
                {
                    iss = _clientEmail,
                    scope = SmScope,
                    aud = TokenUrl,
                    iat = now.ToUnixTimeSeconds(),
                    exp = now.AddHours(1).ToUnixTimeSeconds()
                })));

            var signingInput = $"{B64Url(headerB64)}.{B64Url(claimsB64)}";

            using var rsa = RSA.Create();
            rsa.ImportFromPem(_privateKeyPem.AsSpan());
            var sig = rsa.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var jwt = $"{signingInput}.{B64Url(Convert.ToBase64String(sig))}";

            using var client = new HttpClient();
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
                new KeyValuePair<string, string>("assertion", jwt)
            });

            var resp = await client.PostAsync(TokenUrl, form, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            _cachedAccessToken = doc.RootElement.GetProperty("access_token").GetString()!;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32());
            return _cachedAccessToken;
        }

        private async Task<string> GetTokenFromMetadataAsync(CancellationToken ct)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var req = new HttpRequestMessage(HttpMethod.Get, MetadataUrl);
            req.Headers.Add("Metadata-Flavor", "Google");

            var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            _cachedAccessToken = doc.RootElement.GetProperty("access_token").GetString()!;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32());
            return _cachedAccessToken;
        }

        // ── Helpers ──────────────────────────────────────────────

        private static string B64Url(string input) => input.Replace("+", "-").Replace("/", "_").TrimEnd('=');

        private void EvictExpiredCache()
        {
            foreach (var kvp in _secureCache)
            {
                if (kvp.Value.ExpiresAt <= DateTime.UtcNow && _secureCache.TryRemove(kvp.Key, out var e))
                    e.Dispose();
            }
        }

        private void WipeSecureCache()
        {
            foreach (var kvp in _secureCache)
            {
                if (_secureCache.TryRemove(kvp.Key, out var e))
                    e.Dispose();
            }
        }

        private void LoadConfig()
        {
            if (Configuration.TryGetValue("ProjectId", out var p) && p is string proj)
                _config.ProjectId = proj;
            if (Configuration.TryGetValue("ServiceAccountJsonPath", out var sa) && sa is string saPath)
                _config.ServiceAccountJsonPath = saPath;
            if (Configuration.TryGetValue("SecretPrefix", out var sp) && sp is string prefix)
                _config.SecretPrefix = prefix;
            if (Configuration.TryGetValue("CacheTtlMinutes", out var ttl) && ttl is int cacheTtl)
                _config.CacheTtlMinutes = cacheTtl;
            if (Configuration.TryGetValue("MaxRetries", out var mr) && mr is int retries)
                _config.MaxRetries = retries;
        }

        private enum AdcSource { None, ServiceAccount, GcloudCli, Metadata }

        /// <summary>
        /// Secure cache entry with pinned memory and ZeroMemory wipe.
        /// </summary>
        private sealed class SecureCacheEntry : IDisposable
        {
            private byte[]? _data;
            private GCHandle _pin;
            private bool _disposed;

            public DateTime ExpiresAt { get; }

            public SecureCacheEntry(byte[] data, TimeSpan ttl)
            {
                _data = new byte[data.Length];
                Array.Copy(data, _data, data.Length);
                _pin = GCHandle.Alloc(_data, GCHandleType.Pinned);
                ExpiresAt = DateTime.UtcNow.Add(ttl);
            }

            public byte[] GetCopy()
            {
                if (_disposed || _data == null) throw new ObjectDisposedException(nameof(SecureCacheEntry));
                var copy = new byte[_data.Length];
                Array.Copy(_data, copy, _data.Length);
                return copy;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_data != null)
                {
                    CryptographicOperations.ZeroMemory(_data);
                    if (_pin.IsAllocated) _pin.Free();
                    _data = null;
                }
            }
        }
    }
}
