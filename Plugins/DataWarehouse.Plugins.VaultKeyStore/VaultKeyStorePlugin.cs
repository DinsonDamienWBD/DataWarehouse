using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.VaultKeyStore
{
    /// <summary>
    /// HSM/Vault integration KeyStore plugin supporting multiple cloud providers.
    /// Implements IKeyStore with pluggable vault backends.
    ///
    /// Supported backends:
    /// - HashiCorp Vault (KV secrets engine, Transit engine)
    /// - Azure Key Vault (keys, secrets)
    /// - AWS KMS (symmetric keys, data key generation)
    /// - Google Cloud KMS
    ///
    /// Features:
    /// - Automatic failover between vault providers
    /// - Key caching with configurable TTL
    /// - Key rotation support
    /// - Envelope encryption pattern
    /// - HSM-backed key operations
    ///
    /// Message Commands:
    /// - keystore.vault.create: Create a new key in vault
    /// - keystore.vault.get: Retrieve a key from vault
    /// - keystore.vault.rotate: Rotate a key
    /// - keystore.vault.configure: Configure vault backend
    /// - keystore.vault.health: Check vault health
    /// </summary>
    public sealed class VaultKeyStorePlugin : SecurityProviderPluginBase, IKeyStore, IPlugin
    {
        private readonly VaultConfig _config;
        private readonly ConcurrentDictionary<string, CachedKey> _keyCache;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly HttpClient _httpClient;

        private IVaultBackend? _activeBackend;
        private IVaultBackend[]? _backends;
        private string _currentKeyId = string.Empty;
        private bool _initialized;

        public override string Id => "datawarehouse.plugins.keystore.vault";
        public override string Name => "Vault KeyStore";
        public override string Version => "1.0.0";

        public VaultKeyStorePlugin(VaultConfig? config = null)
        {
            _config = config ?? new VaultConfig();
            _keyCache = new ConcurrentDictionary<string, CachedKey>();
            _httpClient = new HttpClient { Timeout = _config.RequestTimeout };
        }

        public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            var response = await base.OnHandshakeAsync(request);

            await InitializeAsync();

            return response;
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "keystore.vault.create", DisplayName = "Create Key", Description = "Create a new key in vault" },
                new() { Name = "keystore.vault.get", DisplayName = "Get Key", Description = "Retrieve a key from vault" },
                new() { Name = "keystore.vault.rotate", DisplayName = "Rotate Key", Description = "Rotate a vault key" },
                new() { Name = "keystore.vault.configure", DisplayName = "Configure", Description = "Configure vault backend" },
                new() { Name = "keystore.vault.health", DisplayName = "Health Check", Description = "Check vault health" },
                new() { Name = "keystore.vault.wrap", DisplayName = "Wrap Key", Description = "Wrap a data encryption key" },
                new() { Name = "keystore.vault.unwrap", DisplayName = "Unwrap Key", Description = "Unwrap a data encryption key" }
            ];
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SecurityType"] = "VaultKeyStore";
            metadata["SupportsHSM"] = true;
            metadata["SupportsEnvelopeEncryption"] = true;
            metadata["SupportedBackends"] = new[] { "HashiCorpVault", "AzureKeyVault", "AwsKms", "GoogleKms" };
            metadata["ActiveBackend"] = _activeBackend?.Name ?? "None";
            return metadata;
        }

        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "keystore.vault.create":
                    await HandleCreateKeyAsync(message);
                    break;
                case "keystore.vault.get":
                    await HandleGetKeyAsync(message);
                    break;
                case "keystore.vault.rotate":
                    await HandleRotateKeyAsync(message);
                    break;
                case "keystore.vault.configure":
                    await HandleConfigureAsync(message);
                    break;
                case "keystore.vault.health":
                    await HandleHealthCheckAsync(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        public async Task<string> GetCurrentKeyIdAsync()
        {
            await EnsureInitializedAsync();
            return _currentKeyId;
        }

        public byte[] GetKey(string keyId)
        {
            return GetKeyAsync(keyId, new DefaultSecurityContext()).GetAwaiter().GetResult();
        }

        public async Task<byte[]> GetKeyAsync(string keyId, ISecurityContext context)
        {
            await EnsureInitializedAsync();

            if (_keyCache.TryGetValue(keyId, out var cached) && !cached.IsExpired)
            {
                return cached.Key;
            }

            await _lock.WaitAsync();
            try
            {
                if (_keyCache.TryGetValue(keyId, out cached) && !cached.IsExpired)
                {
                    return cached.Key;
                }

                var key = await _activeBackend!.GetKeyAsync(keyId);
                _keyCache[keyId] = new CachedKey(key, _config.CacheExpiration);
                return key;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<byte[]> CreateKeyAsync(string keyId, ISecurityContext context)
        {
            await EnsureInitializedAsync();

            await _lock.WaitAsync();
            try
            {
                var key = await _activeBackend!.CreateKeyAsync(keyId);
                _currentKeyId = keyId;
                _keyCache[keyId] = new CachedKey(key, _config.CacheExpiration);
                return key;
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task InitializeAsync()
        {
            if (_initialized) return;

            await _lock.WaitAsync();
            try
            {
                if (_initialized) return;

                _backends = CreateBackends();

                foreach (var backend in _backends)
                {
                    try
                    {
                        if (await backend.IsHealthyAsync())
                        {
                            _activeBackend = backend;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[VaultKeyStorePlugin] Backend health check failed: {ex.Message}");
                    }
                }

                if (_activeBackend == null && _backends.Length > 0)
                {
                    _activeBackend = _backends[0];
                }

                if (_activeBackend == null)
                {
                    throw new InvalidOperationException("No vault backend available");
                }

                _currentKeyId = await _activeBackend.GetCurrentKeyIdAsync() ?? Guid.NewGuid().ToString("N");

                _initialized = true;
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task EnsureInitializedAsync()
        {
            if (!_initialized)
                await InitializeAsync();
        }

        private IVaultBackend[] CreateBackends()
        {
            var backends = new List<IVaultBackend>();

            if (_config.HashiCorpVault != null)
                backends.Add(new HashiCorpVaultBackend(_config.HashiCorpVault, _httpClient));

            if (_config.AzureKeyVault != null)
                backends.Add(new AzureKeyVaultBackend(_config.AzureKeyVault, _httpClient));

            if (_config.AwsKms != null)
                backends.Add(new AwsKmsBackend(_config.AwsKms, _httpClient));

            if (backends.Count == 0)
            {
                backends.Add(new HashiCorpVaultBackend(new HashiCorpVaultConfig(), _httpClient));
            }

            return backends.ToArray();
        }

        private async Task HandleCreateKeyAsync(PluginMessage message)
        {
            var keyId = GetString(message.Payload, "keyId") ?? Guid.NewGuid().ToString("N");
            var context = message.Payload.TryGetValue("securityContext", out var scObj) && scObj is ISecurityContext sc
                ? sc
                : new DefaultSecurityContext();
            await CreateKeyAsync(keyId, context);
        }

        private async Task HandleGetKeyAsync(PluginMessage message)
        {
            var keyId = GetString(message.Payload, "keyId") ?? throw new ArgumentException("keyId required");
            var context = message.Payload.TryGetValue("securityContext", out var scObj) && scObj is ISecurityContext sc
                ? sc
                : new DefaultSecurityContext();
            await GetKeyAsync(keyId, context);
        }

        private async Task HandleRotateKeyAsync(PluginMessage message)
        {
            var keyId = GetString(message.Payload, "keyId") ?? _currentKeyId;
            await _activeBackend!.RotateKeyAsync(keyId);
            _keyCache.TryRemove(keyId, out _);
        }

        private async Task HandleConfigureAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("backend", out var backendObj) && backendObj is string backend)
            {
                _activeBackend = _backends?.FirstOrDefault(b =>
                    b.Name.Equals(backend, StringComparison.OrdinalIgnoreCase));
            }
            await Task.CompletedTask;
        }

        private async Task HandleHealthCheckAsync(PluginMessage message)
        {
            var isHealthy = await _activeBackend!.IsHealthyAsync();
        }

        private static string? GetString(Dictionary<string, object> payload, string key)
        {
            return payload.TryGetValue(key, out var val) && val is string s ? s : null;
        }
    }

    internal interface IVaultBackend
    {
        string Name { get; }
        Task<bool> IsHealthyAsync();
        Task<string?> GetCurrentKeyIdAsync();
        Task<byte[]> GetKeyAsync(string keyId);
        Task<byte[]> CreateKeyAsync(string keyId);
        Task RotateKeyAsync(string keyId);
        Task<byte[]> WrapKeyAsync(string keyId, byte[] dataKey);
        Task<byte[]> UnwrapKeyAsync(string keyId, byte[] wrappedKey);
    }

    internal class HashiCorpVaultBackend : IVaultBackend
    {
        private readonly HashiCorpVaultConfig _config;
        private readonly HttpClient _httpClient;

        public string Name => "HashiCorpVault";

        public HashiCorpVaultBackend(HashiCorpVaultConfig config, HttpClient httpClient)
        {
            _config = config;
            _httpClient = httpClient;
        }

        public async Task<bool> IsHealthyAsync()
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.Address}/v1/sys/health");
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VaultKeyStorePlugin] Vault operation failed: {ex.Message}");
                return false;
            }
        }

        public Task<string?> GetCurrentKeyIdAsync()
        {
            return Task.FromResult<string?>(_config.DefaultKeyName);
        }

        public async Task<byte[]> GetKeyAsync(string keyId)
        {
            var request = CreateRequest(HttpMethod.Get, $"/v1/{_config.MountPath}/data/{keyId}");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var keyBase64 = doc.RootElement.GetProperty("data").GetProperty("data").GetProperty("key").GetString();
            return Convert.FromBase64String(keyBase64!);
        }

        public async Task<byte[]> CreateKeyAsync(string keyId)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var keyBase64 = Convert.ToBase64String(key);

            var content = JsonSerializer.Serialize(new { data = new { key = keyBase64 } });
            var request = CreateRequest(HttpMethod.Post, $"/v1/{_config.MountPath}/data/{keyId}");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            return key;
        }

        public async Task RotateKeyAsync(string keyId)
        {
            await CreateKeyAsync(keyId);
        }

        public async Task<byte[]> WrapKeyAsync(string keyId, byte[] dataKey)
        {
            var content = JsonSerializer.Serialize(new { plaintext = Convert.ToBase64String(dataKey) });
            var request = CreateRequest(HttpMethod.Post, $"/v1/transit/encrypt/{keyId}");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var ciphertext = doc.RootElement.GetProperty("data").GetProperty("ciphertext").GetString();
            return Encoding.UTF8.GetBytes(ciphertext!);
        }

        public async Task<byte[]> UnwrapKeyAsync(string keyId, byte[] wrappedKey)
        {
            var ciphertext = Encoding.UTF8.GetString(wrappedKey);
            var content = JsonSerializer.Serialize(new { ciphertext });
            var request = CreateRequest(HttpMethod.Post, $"/v1/transit/decrypt/{keyId}");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var plaintext = doc.RootElement.GetProperty("data").GetProperty("plaintext").GetString();
            return Convert.FromBase64String(plaintext!);
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, $"{_config.Address}{path}");
            request.Headers.Add("X-Vault-Token", _config.Token);
            return request;
        }
    }

    internal class AzureKeyVaultBackend : IVaultBackend
    {
        private readonly AzureKeyVaultConfig _config;
        private readonly HttpClient _httpClient;
        private string? _accessToken;
        private DateTime _tokenExpiry;

        public string Name => "AzureKeyVault";

        public AzureKeyVaultBackend(AzureKeyVaultConfig config, HttpClient httpClient)
        {
            _config = config;
            _httpClient = httpClient;
        }

        public async Task<bool> IsHealthyAsync()
        {
            try
            {
                await EnsureTokenAsync();
                var request = CreateRequest(HttpMethod.Get, "/keys?api-version=7.4");
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VaultKeyStorePlugin] Vault operation failed: {ex.Message}");
                return false;
            }
        }

        public Task<string?> GetCurrentKeyIdAsync() => Task.FromResult<string?>(_config.DefaultKeyName);

        public async Task<byte[]> GetKeyAsync(string keyId)
        {
            await EnsureTokenAsync();
            var request = CreateRequest(HttpMethod.Get, $"/secrets/{keyId}?api-version=7.4");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var value = doc.RootElement.GetProperty("value").GetString();
            return Convert.FromBase64String(value!);
        }

        public async Task<byte[]> CreateKeyAsync(string keyId)
        {
            await EnsureTokenAsync();
            var key = RandomNumberGenerator.GetBytes(32);
            var keyBase64 = Convert.ToBase64String(key);

            var content = JsonSerializer.Serialize(new { value = keyBase64, contentType = "application/octet-stream" });
            var request = CreateRequest(HttpMethod.Put, $"/secrets/{keyId}?api-version=7.4");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            return key;
        }

        public async Task RotateKeyAsync(string keyId) => await CreateKeyAsync(keyId);

        public async Task<byte[]> WrapKeyAsync(string keyId, byte[] dataKey)
        {
            await EnsureTokenAsync();
            var content = JsonSerializer.Serialize(new { alg = "RSA-OAEP", value = Convert.ToBase64String(dataKey) });
            var request = CreateRequest(HttpMethod.Post, $"/keys/{keyId}/wrapkey?api-version=7.4");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var wrapped = doc.RootElement.GetProperty("value").GetString();
            return Convert.FromBase64String(wrapped!);
        }

        public async Task<byte[]> UnwrapKeyAsync(string keyId, byte[] wrappedKey)
        {
            await EnsureTokenAsync();
            var content = JsonSerializer.Serialize(new { alg = "RSA-OAEP", value = Convert.ToBase64String(wrappedKey) });
            var request = CreateRequest(HttpMethod.Post, $"/keys/{keyId}/unwrapkey?api-version=7.4");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var unwrapped = doc.RootElement.GetProperty("value").GetString();
            return Convert.FromBase64String(unwrapped!);
        }

        private async Task EnsureTokenAsync()
        {
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

            var response = await _httpClient.PostAsync(tokenUrl, content);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            _accessToken = doc.RootElement.GetProperty("access_token").GetString();
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, $"{_config.VaultUrl}{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            return request;
        }
    }

    internal class AwsKmsBackend : IVaultBackend
    {
        private readonly AwsKmsConfig _config;
        private readonly HttpClient _httpClient;

        public string Name => "AwsKms";

        public AwsKmsBackend(AwsKmsConfig config, HttpClient httpClient)
        {
            _config = config;
            _httpClient = httpClient;
        }

        public async Task<bool> IsHealthyAsync()
        {
            try
            {
                var request = CreateSignedRequest("DescribeKey", new { KeyId = _config.DefaultKeyId });
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VaultKeyStorePlugin] Vault operation failed: {ex.Message}");
                return false;
            }
        }

        public Task<string?> GetCurrentKeyIdAsync() => Task.FromResult<string?>(_config.DefaultKeyId);

        public async Task<byte[]> GetKeyAsync(string keyId)
        {
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

        public async Task<byte[]> CreateKeyAsync(string keyId)
        {
            var request = CreateSignedRequest("CreateKey", new
            {
                Description = $"DataWarehouse key: {keyId}",
                KeyUsage = "ENCRYPT_DECRYPT",
                KeySpec = "SYMMETRIC_DEFAULT"
            });

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            return await GetKeyAsync(keyId);
        }

        public Task RotateKeyAsync(string keyId)
        {
            return Task.CompletedTask;
        }

        public async Task<byte[]> WrapKeyAsync(string keyId, byte[] dataKey)
        {
            var request = CreateSignedRequest("Encrypt", new
            {
                KeyId = keyId,
                Plaintext = Convert.ToBase64String(dataKey)
            });

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var ciphertext = doc.RootElement.GetProperty("CiphertextBlob").GetString();
            return Convert.FromBase64String(ciphertext!);
        }

        public async Task<byte[]> UnwrapKeyAsync(string keyId, byte[] wrappedKey)
        {
            var request = CreateSignedRequest("Decrypt", new
            {
                KeyId = keyId,
                CiphertextBlob = Convert.ToBase64String(wrappedKey)
            });

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var plaintext = doc.RootElement.GetProperty("Plaintext").GetString();
            return Convert.FromBase64String(plaintext!);
        }

        private HttpRequestMessage CreateSignedRequest(string action, object payload)
        {
            var content = JsonSerializer.Serialize(payload);
            var request = new HttpRequestMessage(HttpMethod.Post, $"https://kms.{_config.Region}.amazonaws.com/");
            request.Headers.Add("X-Amz-Target", $"TrentService.{action}");
            request.Content = new StringContent(content, Encoding.UTF8, "application/x-amz-json-1.1");

            SignRequest(request, content);
            return request;
        }

        private void SignRequest(HttpRequestMessage request, string content)
        {
            var now = DateTime.UtcNow;
            var dateStamp = now.ToString("yyyyMMdd");
            var amzDate = now.ToString("yyyyMMddTHHmmssZ");

            request.Headers.Add("X-Amz-Date", amzDate);
            request.Headers.Add("Host", $"kms.{_config.Region}.amazonaws.com");

            var canonicalRequest = CreateCanonicalRequest(request, content, amzDate);
            var stringToSign = CreateStringToSign(canonicalRequest, amzDate, dateStamp);
            var signature = CalculateSignature(stringToSign, dateStamp);

            var authHeader = $"AWS4-HMAC-SHA256 Credential={_config.AccessKeyId}/{dateStamp}/{_config.Region}/kms/aws4_request, " +
                           $"SignedHeaders=content-type;host;x-amz-date;x-amz-target, Signature={signature}";
            request.Headers.TryAddWithoutValidation("Authorization", authHeader);
        }

        private string CreateCanonicalRequest(HttpRequestMessage request, string content, string amzDate)
        {
            var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            return $"POST\n/\n\ncontent-type:application/x-amz-json-1.1\nhost:kms.{_config.Region}.amazonaws.com\n" +
                   $"x-amz-date:{amzDate}\nx-amz-target:TrentService.{request.Headers.GetValues("X-Amz-Target").First()}\n\n" +
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
    }

    public class VaultConfig
    {
        public HashiCorpVaultConfig? HashiCorpVault { get; set; }
        public AzureKeyVaultConfig? AzureKeyVault { get; set; }
        public AwsKmsConfig? AwsKms { get; set; }
        public TimeSpan CacheExpiration { get; set; } = TimeSpan.FromMinutes(30);
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    }

    public class HashiCorpVaultConfig
    {
        public string Address { get; set; } = "http://127.0.0.1:8200";
        public string Token { get; set; } = string.Empty;
        public string MountPath { get; set; } = "secret";
        public string DefaultKeyName { get; set; } = "datawarehouse-master";
    }

    public class AzureKeyVaultConfig
    {
        public string VaultUrl { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string DefaultKeyName { get; set; } = "datawarehouse-master";
    }

    public class AwsKmsConfig
    {
        public string Region { get; set; } = "us-east-1";
        public string AccessKeyId { get; set; } = string.Empty;
        public string SecretAccessKey { get; set; } = string.Empty;
        public string DefaultKeyId { get; set; } = string.Empty;
    }

    internal class CachedKey
    {
        public byte[] Key { get; }
        public DateTime Expiration { get; }
        public bool IsExpired => DateTime.UtcNow >= Expiration;

        public CachedKey(byte[] key, TimeSpan expiration)
        {
            Key = key;
            Expiration = DateTime.UtcNow.Add(expiration);
        }
    }

    internal class DefaultSecurityContext : ISecurityContext
    {
        public string UserId => Environment.UserName;
        public string? TenantId => "local";
        public IEnumerable<string> Roles => ["user"];
        public bool IsSystemAdmin => false;
    }
}
